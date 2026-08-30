import { EventEmitter } from 'node:events';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppRuntimeManager, type AppRuntimeWorker } from '../../src/main/services/app-runtime-manager';
import type { AppManifestV1 } from '../../src/shared/app-contract';

const manifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'analysis-center',
  name: '分析中心',
  description: '诊断包与日志报告',
  publisherId: 'thelinyue',
  version: '1.0.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.0',
  runtime: { rendererEntry: 'renderer/index.html', backendEntry: 'backend/entry.js', icon: 'renderer/icon.png' },
  capabilities: ['file.open']
};

const startOptions = (appId = 'analysis-center') => ({
  appId,
  installPath: `D:/apps/${appId}/1.0.0`,
  dataDirectory: `D:/data/${appId}`,
  manifest: { ...manifest, id: appId }
});

afterEach(() => vi.useRealTimers());

describe('应用运行时管理器', () => {
  it('启动纯 Web 应用时不创建 backend Worker', async () => {
    let workerCreated = false;
    const manager = new AppRuntimeManager({ createWorker: () => { workerCreated = true; return new FakeWorker(); } });
    const webManifest = { ...manifest, id: 'lvm-uncache-tool', runtime: { kind: 'web' as const, rendererEntry: 'index.html', icon: 'icon.svg' } };

    await manager.start({ appId: 'lvm-uncache-tool', installPath: 'D:/apps/lvm-uncache-tool/1.0.0', dataDirectory: 'D:/data/lvm-uncache-tool', manifest: webManifest });

    expect(workerCreated).toBe(false);
    expect(manager.getState('lvm-uncache-tool')).toBe('running');
    await expect(manager.invoke('lvm-uncache-tool', 'anything', null)).rejects.toThrow('不支持 backend');
    await expect(manager.stop('lvm-uncache-tool')).resolves.toBeUndefined();
    expect(manager.getState('lvm-uncache-tool')).toBe('stopped');
  });

  it('backend ready 前处于 starting 且拒绝 invoke，ready 后才进入 running', async () => {
    const worker = new FakeWorker(false);
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    const starting = manager.start(startOptions());

    expect(manager.getState('analysis-center')).toBe('starting');
    await expect(manager.invoke('analysis-center', 'packages.list', null)).rejects.toThrow('尚未运行');
    worker.respond({ type: 'ready' });
    await starting;
    expect(manager.getState('analysis-center')).toBe('running');
  });

  it('同一应用并发 start 共享同一个 Promise 和 Worker', async () => {
    const worker = new FakeWorker(false);
    let createCalls = 0;
    const manager = new AppRuntimeManager({ createWorker: () => { createCalls += 1; return worker; } });
    const first = manager.start(startOptions());
    const second = manager.start(startOptions());

    expect(first).toBe(second);
    expect(createCalls).toBe(1);
    expect(manager.getState('analysis-center')).toBe('starting');
    worker.respond({ type: 'ready' });
    await Promise.all([first, second]);
    expect(manager.getState('analysis-center')).toBe('running');
  });

  it('Worker 启动异常、提前退出和超时都会进入 failed', async () => {
    const errorWorker = new FakeWorker(false);
    const errorManager = new AppRuntimeManager({ createWorker: () => errorWorker, startTimeoutMs: 50 });
    const errored = errorManager.start(startOptions());
    errorWorker.emit('error', new Error('端口断开'));
    await expect(errored).rejects.toThrow('Worker 异常：端口断开');
    expect(errorManager.getState('analysis-center')).toBe('failed');

    const exitedWorker = new FakeWorker(false);
    const exitManager = new AppRuntimeManager({ createWorker: () => exitedWorker, startTimeoutMs: 50 });
    const exited = exitManager.start(startOptions());
    exitedWorker.emit('exit', 0);
    await expect(exited).rejects.toThrow('启动前退出，退出码：0');
    expect(exitManager.getState('analysis-center')).toBe('failed');

    vi.useFakeTimers();
    const timeoutWorker = new FakeWorker(false);
    const timeoutManager = new AppRuntimeManager({ createWorker: () => timeoutWorker, startTimeoutMs: 10, logger: { error: () => undefined } });
    const timedOut = timeoutManager.start(startOptions());
    await vi.advanceTimersByTimeAsync(10);
    await expect(timedOut).rejects.toThrow('启动超时（10ms）');
    expect(timeoutWorker.terminateCalls).toBe(1);
    expect(timeoutManager.getState('analysis-center')).toBe('failed');
  });

  it('把应用 RPC 转发给对应 Worker 并返回结果和事件', async () => {
    const worker = new FakeWorker();
    const events: unknown[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    manager.onEvent((event) => events.push(event));
    await manager.start(startOptions());

    const result = manager.invoke('analysis-center', 'packages.list', { page: 1 });
    worker.respond({ type: 'event', appId: 'analysis-center', event: 'tasks.changed', payload: { count: 1 } });
    worker.respond({ type: 'response', requestId: worker.lastRequestId, ok: true, result: ['package-1'] });

    await expect(result).resolves.toEqual(['package-1']);
    expect(events).toEqual([{ appId: 'analysis-center', event: 'tasks.changed', payload: { count: 1 } }]);
  });

  it('只允许声明 notification.show 的 backend 以运行时身份发送通知', async () => {
    const worker = new FakeWorker();
    const notifications: unknown[] = [];
    const errors: string[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker, logger: { error: (message) => errors.push(message) } });
    manager.onNotification((notification) => notifications.push(notification));
    await manager.start({
      ...startOptions(),
      manifest: { ...manifest, capabilities: [...manifest.capabilities, 'notification.show'] }
    });

    worker.respond({
      type: 'notification',
      appId: 'spoofed-app',
      payload: { title: '分析完成', body: 'diagnostic.tgz', windowKey: 'main', activationPayload: { packageId: 'package-1' } }
    });

    expect(notifications).toEqual([{
      appId: 'analysis-center',
      payload: { title: '分析完成', body: 'diagnostic.tgz', windowKey: 'main', activationPayload: { packageId: 'package-1' } }
    }]);
    expect(errors).toEqual([]);
  });

  it('拒绝未声明 notification.show 的 backend 通知请求并输出中文日志', async () => {
    const worker = new FakeWorker();
    const notifications: unknown[] = [];
    const errors: string[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker, logger: { error: (message) => errors.push(message) } });
    manager.onNotification((notification) => notifications.push(notification));
    await manager.start(startOptions());

    worker.respond({ type: 'notification', payload: { title: '不应显示', body: '未授权通知' } });

    expect(notifications).toEqual([]);
    expect(errors).toEqual(['应用 analysis-center 未声明 notification.show，已拒绝 backend 通知请求。']);
  });

  it('收到成功停止确认后自然结束，不调用 terminate', async () => {
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    await manager.start(startOptions());

    const stopping = manager.stop('analysis-center');
    expect(worker.messages).toEqual([{ type: 'stop' }]);
    worker.respond({ type: 'stopped', ok: true });

    await expect(stopping).resolves.toBeUndefined();
    expect(worker.terminateCalls).toBe(0);
    expect(manager.getState('analysis-center')).toBe('stopped');
  });

  it('默认恰好等待 5000ms 后才强制终止', async () => {
    vi.useFakeTimers();
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker, logger: { error: () => undefined } });
    await manager.start(startOptions());

    const stopping = manager.stop('analysis-center');
    expect(manager.getState('analysis-center')).toBe('stopping');
    let settled = false;
    void stopping.then(() => { settled = true; }, () => { settled = true; });
    await vi.advanceTimersByTimeAsync(4_999);
    expect(worker.terminateCalls).toBe(0);
    expect(settled).toBe(false);
    await vi.advanceTimersByTimeAsync(1);

    await expect(stopping).rejects.toThrow('5000');
    expect(worker.terminateCalls).toBe(1);
  });

  it('可注入短超时，超时强制终止并输出包含应用名的中文错误', async () => {
    const worker = new FakeWorker();
    const errors: string[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker, stopTimeoutMs: 5, logger: { error: (message) => errors.push(message) } });
    await manager.start(startOptions());

    await expect(manager.stop('analysis-center')).rejects.toThrow('等待应用 analysis-center 清理确认超时（5ms）');

    expect(worker.terminateCalls).toBe(1);
    expect(errors).toEqual(['应用 analysis-center 在 5ms 内未完成清理，正在强制终止 Worker。']);
  });

  it('负确认报告清理错误，且不会误报超时或强制终止', async () => {
    const worker = new FakeWorker();
    const errors: string[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker, stopTimeoutMs: 5, logger: { error: (message) => errors.push(message) } });
    await manager.start(startOptions());

    const stopping = manager.stop('analysis-center');
    worker.respond({ type: 'stopped', ok: false, errorMessage: '关闭数据库失败' });

    await expect(stopping).rejects.toThrow('应用 analysis-center 清理失败：关闭数据库失败');
    expect(worker.terminateCalls).toBe(0);
    expect(errors.join('\n')).not.toContain('超时');
  });

  it.each([
    ['异常', (worker: FakeWorker) => worker.emit('error', new Error('端口断开')), 'Worker 异常：端口断开'],
    ['退出', (worker: FakeWorker) => worker.emit('exit', 7), '在清理确认前退出，退出码：7']
  ])('Worker 在确认前%s会立即结束停止等待', async (_label, finishWorker, expected) => {
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker, stopTimeoutMs: 50 });
    await manager.start(startOptions());

    const stopping = manager.stop('analysis-center');
    finishWorker(worker);

    await expect(stopping).rejects.toThrow(expected);
    expect(worker.terminateCalls).toBe(0);
  });

  it('同一应用的并发 stop 共享一次停止消息和结果', async () => {
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    await manager.start(startOptions());

    const first = manager.stop('analysis-center');
    const second = manager.stop('analysis-center');
    expect(worker.messages).toEqual([{ type: 'stop' }]);
    worker.respond({ type: 'stopped', ok: true });

    await expect(Promise.all([first, second])).resolves.toEqual([undefined, undefined]);
  });

  it('stopAll 会尝试停止全部应用，并在全部结算后汇总中文错误', async () => {
    const first = new FakeWorker();
    const second = new FakeWorker();
    const workers = [first, second];
    const manager = new AppRuntimeManager({ createWorker: () => workers.shift()! });
    await manager.start(startOptions('analysis-center'));
    await manager.start(startOptions('terminal'));

    const stopping = manager.stopAll();
    expect(first.messages).toEqual([{ type: 'stop' }]);
    expect(second.messages).toEqual([{ type: 'stop' }]);
    first.respond({ type: 'stopped', ok: false, errorMessage: '数据库忙' });
    second.respond({ type: 'stopped', ok: true });

    await expect(stopping).rejects.toThrow('停止应用运行时失败：analysis-center');
    expect(second.terminateCalls).toBe(0);
  });

  it('重载应用等待旧 Worker 确认后才创建替代 Worker', async () => {
    const first = new FakeWorker();
    const second = new FakeWorker();
    const workers = [first, second];
    const created: FakeWorker[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => { const worker = workers.shift()!; created.push(worker); return worker; } });
    await manager.start(startOptions());

    const restarting = manager.restart({ ...startOptions(), installPath: 'D:/dev/analysis-center/dist' });
    expect(created).toEqual([first]);
    first.respond({ type: 'stopped', ok: true });
    await restarting;

    expect(created).toEqual([first, second]);
  });

  it('停止开始即拒绝已有和新 RPC，不把请求发送给正在清理的 Worker', async () => {
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    await manager.start(startOptions());
    const pending = manager.invoke('analysis-center', 'packages.list', null);

    const stopping = manager.stop('analysis-center');

    await expect(pending).rejects.toThrow('应用正在停止：analysis-center');
    await expect(manager.invoke('analysis-center', 'packages.list', null)).rejects.toThrow('尚未启动');
    expect(worker.messages.map((message) => message.type)).toEqual(['invoke', 'stop']);
    worker.respond({ type: 'stopped', ok: true });
    await stopping;
  });
});

class FakeWorker extends EventEmitter implements AppRuntimeWorker {
  public lastRequestId = '';
  public terminateCalls = 0;
  public readonly messages: Array<{ type: string; requestId?: string }> = [];
  private readyScheduled = false;

  public constructor(private readonly autoReady = true) { super(); }

  public override on(event: string | symbol, listener: (...args: any[]) => void): this {
    super.on(event, listener);
    if (event === 'message' && this.autoReady && !this.readyScheduled) {
      this.readyScheduled = true;
      queueMicrotask(() => this.respond({ type: 'ready' }));
    }
    return this;
  }

  public postMessage(message: { type: string; requestId?: string }): void {
    this.messages.push(message);
    this.lastRequestId = message.requestId ?? '';
  }

  public terminate(): Promise<number> {
    this.terminateCalls += 1;
    return Promise.resolve(0);
  }

  public respond(message: unknown): void { this.emit('message', message); }
}
