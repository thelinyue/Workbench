import { EventEmitter } from 'node:events';
import { describe, expect, it } from 'vitest';
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

describe('应用运行时管理器', () => {
  it('启动纯 Web 应用时不创建 backend Worker', async () => {
    let workerCreated = false;
    const manager = new AppRuntimeManager({ createWorker: () => { workerCreated = true; return new FakeWorker(); } });
    const webManifest = { ...manifest, id: 'lvm-uncache-tool', runtime: { kind: 'web' as const, rendererEntry: 'index.html', icon: 'icon.svg' } };

    await manager.start({ appId: 'lvm-uncache-tool', installPath: 'D:/apps/lvm-uncache-tool/1.0.0', dataDirectory: 'D:/data/lvm-uncache-tool', manifest: webManifest });

    expect(workerCreated).toBe(false);
    await expect(manager.invoke('lvm-uncache-tool', 'anything', null)).rejects.toThrow('不支持 backend');
  });

  it('把应用 RPC 转发给对应 Worker 并返回结果和事件', async () => {
    const worker = new FakeWorker();
    const events: unknown[] = [];
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    manager.onEvent((event) => events.push(event));
    await manager.start({ appId: 'analysis-center', installPath: 'D:/apps/analysis-center/1.0.0', dataDirectory: 'D:/data/analysis-center', manifest });

    const result = manager.invoke('analysis-center', 'packages.list', { page: 1 });
    worker.respond({ type: 'event', appId: 'analysis-center', event: 'tasks.changed', payload: { count: 1 } });
    worker.respond({ type: 'response', requestId: worker.lastRequestId, ok: true, result: ['package-1'] });

    await expect(result).resolves.toEqual(['package-1']);
    expect(events).toEqual([{ appId: 'analysis-center', event: 'tasks.changed', payload: { count: 1 } }]);
  });

  it('停止应用时终止 Worker，并拒绝后续请求', async () => {
    const worker = new FakeWorker();
    const manager = new AppRuntimeManager({ createWorker: () => worker });
    await manager.start({ appId: 'analysis-center', installPath: 'D:/apps/analysis-center/1.0.0', dataDirectory: 'D:/data/analysis-center', manifest });

    await manager.stop('analysis-center');

    expect(worker.terminated).toBe(true);
    await expect(manager.invoke('analysis-center', 'packages.list', null)).rejects.toThrow('尚未启动');
  });
});

class FakeWorker extends EventEmitter implements AppRuntimeWorker {
  public lastRequestId = '';
  public terminated = false;

  public postMessage(message: { type: string; requestId?: string }): void {
    this.lastRequestId = message.requestId ?? '';
  }

  public terminate(): Promise<number> {
    this.terminated = true;
    return Promise.resolve(0);
  }

  public respond(message: unknown): void { this.emit('message', message); }
}
