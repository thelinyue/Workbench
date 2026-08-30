import { describe, expect, it, vi } from 'vitest';
import { WorkbenchIpcBoundary, createWorkbenchIpcCleanup, type IpcHandlerHost } from '../../src/main/services/workbench-ipc-shutdown';

describe('Workbench IPC 退出边界', () => {
  it('cleanup 同步封闭 launch/reload，drain 在途 handler 后等待已有 runtime，再关闭仓储', async () => {
    const ipc = new FakeIpcHost();
    const boundary = new WorkbenchIpcBoundary(ipc);
    const manifestGate = deferred<void>();
    const reloadStopGate = deferred<void>();
    const runtimeStop = deferred<void>();
    const events: string[] = [];
    let newRuntimeStarts = 0;
    let registryClosed = false;
    let layoutClosed = false;

    boundary.handle('apps:launch', async () => {
      events.push('launch-entered');
      await manifestGate.promise;
      boundary.ensureOpen();
      newRuntimeStarts += 1;
    });
    boundary.handle('apps:reload', async () => {
      events.push('reload-entered');
      await reloadStopGate.promise;
      boundary.ensureOpen();
      newRuntimeStarts += 1;
    });
    const cleanup = createWorkbenchIpcCleanup({
      ipcBoundary: boundary,
      unregisterWindowShellIpc: () => { events.push('shell-closed'); },
      unregisterRuntimeEvents: () => { events.push('events-closed'); },
      waitForInitialization: async () => { events.push('initialized'); },
      stopAllRuntimes: async () => { events.push('runtime-stop'); await runtimeStop.promise; events.push('runtime-stopped'); },
      closeAppRegistry: () => { registryClosed = true; events.push('registry-closed'); },
      closeDesktopRepository: () => { layoutClosed = true; events.push('layout-closed'); }
    });

    const launching = ipc.invoke('apps:launch');
    const reloading = ipc.invoke('apps:reload');
    await vi.waitFor(() => expect(events).toEqual(['launch-entered', 'reload-entered']));
    const firstCleanup = cleanup();
    const secondCleanup = cleanup();

    expect(firstCleanup).toBe(secondCleanup);
    await expect(ipc.invoke('apps:reload')).rejects.toThrow('IPC handler 未注册：apps:reload');
    manifestGate.resolve();
    reloadStopGate.resolve();
    await expect(launching).rejects.toThrow('Workbench 正在退出，不能继续处理 IPC 请求');
    await expect(reloading).rejects.toThrow('Workbench 正在退出，不能继续处理 IPC 请求');
    await vi.waitFor(() => expect(events).toContain('runtime-stop'));
    expect(newRuntimeStarts).toBe(0);
    expect(registryClosed).toBe(false);
    expect(layoutClosed).toBe(false);

    runtimeStop.resolve();
    await firstCleanup;
    expect(events).toEqual([
      'launch-entered', 'reload-entered', 'shell-closed', 'events-closed', 'initialized',
      'runtime-stop', 'runtime-stopped', 'registry-closed', 'layout-closed'
    ]);
  });

  it('cleanup 等待同一个 initialization Promise 后才停止 runtime', async () => {
    const ipc = new FakeIpcHost();
    const boundary = new WorkbenchIpcBoundary(ipc);
    const initializationGate = deferred<void>();
    const events: string[] = [];
    let runtimeStopped = false;
    const cleanup = createWorkbenchIpcCleanup({
      ipcBoundary: boundary,
      unregisterWindowShellIpc: () => undefined,
      unregisterRuntimeEvents: () => undefined,
      waitForInitialization: async () => { events.push('initialization-waited'); await initializationGate.promise; events.push('initialized'); },
      stopAllRuntimes: async () => { runtimeStopped = true; events.push('runtime-stopped'); },
      closeAppRegistry: () => undefined,
      closeDesktopRepository: () => undefined
    });

    const pending = cleanup();
    await vi.waitFor(() => expect(events).toEqual(['initialization-waited']));
    expect(runtimeStopped).toBe(false);

    initializationGate.resolve();
    await pending;
    expect(events).toEqual(['initialization-waited', 'initialized', 'runtime-stopped']);
  });
});

class FakeIpcHost implements IpcHandlerHost {
  private readonly handlers = new Map<string, (...args: unknown[]) => unknown>();
  public handle(channel: string, handler: (...args: unknown[]) => unknown): void { this.handlers.set(channel, handler); }
  public removeHandler(channel: string): void { this.handlers.delete(channel); }
  public invoke(channel: string, ...args: unknown[]): Promise<unknown> {
    const handler = this.handlers.get(channel);
    if (!handler) return Promise.reject(new Error(`IPC handler 未注册：${channel}`));
    return Promise.resolve(handler(...args));
  }
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>((accept) => { resolve = accept; }), resolve };
}
