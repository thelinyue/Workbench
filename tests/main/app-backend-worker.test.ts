import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';
import { createBackendWorkerSession, type AppBackend, type BackendWorkerPort } from '../../src/main/services/app-backend-worker-session';

describe('应用 backend Worker 停止边界', () => {
  it('等待在途 invoke 和异步 close 后只发送一次成功确认并关闭端口', async () => {
    const invoke = deferred<unknown>();
    const close = deferred<void>();
    const events: string[] = [];
    const port = new FakePort();
    const backend: AppBackend = {
      invoke: async () => { events.push('invoke'); return invoke.promise; },
      close: async () => { events.push('close'); await close.promise; events.push('closed'); }
    };
    createBackendWorkerSession(port, backend);

    port.receive({ type: 'invoke', requestId: 'request-1', method: 'packages.list', payload: null });
    port.receive({ type: 'stop' });
    port.receive({ type: 'stop' });
    await Promise.resolve();
    expect(events).toEqual(['invoke']);
    expect(port.messages).toEqual([]);

    invoke.resolve(['package-1']);
    await vi.waitFor(() => expect(events).toEqual(['invoke', 'close']));
    expect(port.messages).toEqual([{ type: 'response', requestId: 'request-1', ok: true, result: ['package-1'] }]);

    close.resolve();
    await vi.waitFor(() => expect(port.closed).toBe(true));
    expect(events).toEqual(['invoke', 'close', 'closed']);
    expect(port.messages.filter((message) => message.type === 'stopped')).toEqual([{ type: 'stopped', ok: true }]);
  });

  it('在途 invoke 失败时保留普通失败响应，并发送一次中文负确认', async () => {
    const invoke = deferred<unknown>();
    const port = new FakePort();
    createBackendWorkerSession(port, { invoke: () => invoke.promise, close: () => undefined });

    port.receive({ type: 'invoke', requestId: 'request-1', method: 'analysis.start', payload: null });
    port.receive({ type: 'stop' });
    invoke.reject(new Error('分析线程失败'));

    await vi.waitFor(() => expect(port.closed).toBe(true));
    expect(port.messages).toContainEqual({ type: 'response', requestId: 'request-1', ok: false, errorMessage: '分析线程失败' });
    expect(port.messages.filter((message) => message.type === 'stopped')).toEqual([
      { type: 'stopped', ok: false, errorMessage: '应用 backend 关闭失败：在途请求失败：分析线程失败' }
    ]);
  });

  it('backend.close 失败时发送中文负确认并关闭端口', async () => {
    const port = new FakePort();
    createBackendWorkerSession(port, { invoke: () => undefined, close: async () => { throw new Error('SQLite 无法关闭'); } });

    port.receive({ type: 'stop' });

    await vi.waitFor(() => expect(port.closed).toBe(true));
    expect(port.messages).toEqual([{ type: 'stopped', ok: false, errorMessage: '应用 backend 关闭失败：SQLite 无法关闭' }]);
  });

  it('停止开始后拒绝新 invoke，且不再调用 backend', async () => {
    const port = new FakePort();
    let invokeCalls = 0;
    const close = deferred<void>();
    createBackendWorkerSession(port, { invoke: () => { invokeCalls += 1; }, close: () => close.promise });

    port.receive({ type: 'stop' });
    port.receive({ type: 'invoke', requestId: 'late-request', method: 'packages.list', payload: null });

    expect(invokeCalls).toBe(0);
    expect(port.messages).toContainEqual({ type: 'response', requestId: 'late-request', ok: false, errorMessage: '应用 backend 正在关闭，不能接受新请求' });
    close.resolve();
    await vi.waitFor(() => expect(port.closed).toBe(true));
  });
});

class FakePort extends EventEmitter implements BackendWorkerPort {
  public readonly messages: Array<Record<string, unknown>> = [];
  public closed = false;

  public postMessage(message: Record<string, unknown>): void { this.messages.push(message); }
  public close(): void { this.closed = true; }
  public receive(message: unknown): void { this.emit('message', message); }
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void; reject(error: Error): void } {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  return { promise: new Promise<T>((accept, decline) => { resolve = accept; reject = decline; }), resolve, reject };
}
