import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';
import { WorkbenchLifecycleController, acquireSingleInstance, createOrderedCleanup, type DesktopMainWindow } from '../../src/main/services/workbench-lifecycle';

describe('Workbench 原生窗口与进程生命周期', () => {
  it('获取单实例锁失败时立即退出，成功时保留首实例', () => {
    const secondary = new FakeApp(false);
    const primary = new FakeApp(true);

    expect(acquireSingleInstance(secondary)).toBe(false);
    expect(secondary.quitCalls).toBe(1);
    expect(acquireSingleInstance(primary)).toBe(true);
    expect(primary.quitCalls).toBe(0);
  });

  it('主窗口普通 close 只隐藏到托盘，不退出进程或触发清理', () => {
    const app = new FakeApp();
    const cleanup = vi.fn(async () => undefined);
    const controller = createController(app, () => new FakeWindow(), cleanup);
    const main = controller.openMainWindow() as FakeWindow;

    const closeEvent = { preventDefault: vi.fn() };
    main.emit('close', closeEvent);
    app.emit('window-all-closed');

    expect(app.quitCalls).toBe(0);
    expect(cleanup).not.toHaveBeenCalled();
    expect(closeEvent.preventDefault).toHaveBeenCalledOnce();
    expect(main.hideCalls).toBe(1);
    expect(main.closed).toBe(false);
  });

  it('second-instance 和 activate 会聚焦现有主窗口，关闭后则重建主窗口', () => {
    const app = new FakeApp();
    const windows: FakeWindow[] = [];
    const controller = createController(app, () => { const window = new FakeWindow(); windows.push(window); return window; });
    const first = controller.openMainWindow() as FakeWindow;

    app.emit('second-instance');
    expect(first.showCalls).toBe(1);
    expect(first.focusCalls).toBe(1);
    expect(windows).toHaveLength(1);

    first.emit('closed');
    app.emit('activate');
    expect(windows).toHaveLength(2);

    windows[1]!.emit('closed');
    app.emit('second-instance');
    expect(windows).toHaveLength(3);
  });

  it('聚焦已最小化的主窗口前先恢复', () => {
    const app = new FakeApp();
    const main = new FakeWindow();
    main.minimized = true;
    const controller = createController(app, () => main);
    controller.openMainWindow();

    app.emit('activate');

    expect(main.events).toEqual(['restore', 'show', 'focus']);
  });

  it('退出清理开始后恢复入口不创建、显示或聚焦主窗口', () => {
    const app = new FakeApp();
    const cleanupGate = deferred<void>();
    const windows: FakeWindow[] = [];
    const controller = createController(app, () => { const window = new FakeWindow(); windows.push(window); return window; }, () => cleanupGate.promise);
    const main = controller.openMainWindow() as FakeWindow;
    const quitEvent = { preventDefault: vi.fn() };

    app.emit('before-quit', quitEvent);
    controller.restoreMainWindow();

    expect(windows).toHaveLength(1);
    expect(main.showCalls).toBe(0);
    expect(main.focusCalls).toBe(0);
    cleanupGate.resolve();
  });

  it('托盘不可用时普通 close 不隐藏窗口而请求 app.quit', () => {
    const app = new FakeApp();
    const main = new FakeWindow();
    const controller = createController(app, () => main, async () => undefined, () => false);
    controller.openMainWindow();
    const closeEvent = { preventDefault: vi.fn() };

    main.emit('close', closeEvent);

    expect(closeEvent.preventDefault).not.toHaveBeenCalled();
    expect(main.hideCalls).toBe(0);
    expect(app.quitCalls).toBe(1);
  });

  it('window-all-closed 在 Windows/Linux 托盘驻留，macOS 也保持应用存活', () => {
    const windowsApp = new FakeApp();
    createController(windowsApp, () => new FakeWindow());
    windowsApp.emit('window-all-closed');
    expect(windowsApp.quitCalls).toBe(0);

    const macApp = new FakeApp();
    createController(macApp, () => new FakeWindow());
    macApp.emit('window-all-closed');
    expect(macApp.quitCalls).toBe(0);
  });

  it('before-quit 首次和等待期间均阻止退出，只启动一次清理，完成后的最终事件直接放行', async () => {
    const app = new FakeApp();
    const cleanupGate = deferred<void>();
    const cleanup = vi.fn(() => cleanupGate.promise);
    createController(app, () => new FakeWindow(), cleanup);
    const firstEvent = { preventDefault: vi.fn() };
    const repeatedEvent = { preventDefault: vi.fn() };

    app.emit('before-quit', firstEvent);
    app.emit('before-quit', repeatedEvent);

    expect(firstEvent.preventDefault).toHaveBeenCalledOnce();
    expect(repeatedEvent.preventDefault).toHaveBeenCalledOnce();
    expect(cleanup).toHaveBeenCalledOnce();
    expect(app.quitCalls).toBe(0);

    cleanupGate.resolve();
    await vi.waitFor(() => expect(app.quitCalls).toBe(1));
    const finalEvent = { preventDefault: vi.fn() };
    app.emit('before-quit', finalEvent);
    expect(finalEvent.preventDefault).not.toHaveBeenCalled();
    expect(cleanup).toHaveBeenCalledOnce();
  });

  it('最终退出用 destroy 绕过被取消的主窗口 close，并幂等清空已跟踪窗口', () => {
    const app = new FakeApp();
    const windows: FakeWindow[] = [];
    const controller = createController(app, () => {
      const window = new FakeWindow();
      windows.push(window);
      return window;
    });
    const main = controller.openMainWindow() as FakeWindow;
    main.preventClose = true;

    main.close();
    expect(main.closed).toBe(false);

    controller.destroyMainWindowForShutdown();
    controller.destroyMainWindowForShutdown();

    expect(main.closeCalls).toBe(1);
    expect(main.destroyCalls).toBe(1);
    expect(main.closed).toBe(true);
    expect(controller.openMainWindow()).toBe(windows[1]);
  });

  it('主窗口强制销毁严格发生在 runtime drain 后、协议和仓储关闭前', async () => {
    const app = new FakeApp();
    const runtimeGate = deferred<void>();
    const events: string[] = [];
    let controller!: WorkbenchLifecycleController;
    const cleanup = createOrderedCleanup([
      { name: 'Workbench IPC 与应用运行时', close: async () => { events.push('runtime-start'); await runtimeGate.promise; events.push('runtime-drained'); } },
      { name: 'Workbench 主窗口', close: () => { controller.destroyMainWindowForShutdown(); } },
      { name: '应用资源协议', close: () => { events.push('protocol-closed'); } },
      { name: '应用窗口状态仓储', close: () => { events.push('repository-closed'); } }
    ]);
    const main = new FakeWindow(() => events.push('main-destroyed'));
    main.preventClose = true;
    controller = createController(app, () => main, cleanup);
    controller.openMainWindow();
    const quitEvent = { preventDefault: vi.fn() };

    app.emit('before-quit', quitEvent);
    expect(events).toEqual(['runtime-start']);
    expect(main.destroyCalls).toBe(0);

    runtimeGate.resolve();
    await vi.waitFor(() => expect(app.quitCalls).toBe(1));

    expect(events).toEqual(['runtime-start', 'runtime-drained', 'main-destroyed', 'protocol-closed', 'repository-closed']);
    expect(main.closeCalls).toBe(0);
    expect(main.destroyCalls).toBe(1);
  });

  it('有序清理严格等待 Workbench、协议、窗口状态仓储，并且并发调用共享 Promise', async () => {
    const workbenchGate = deferred<void>();
    const events: string[] = [];
    const cleanup = createOrderedCleanup([
      { name: 'Workbench IPC 与应用运行时', close: async () => { events.push('workbench'); await workbenchGate.promise; events.push('runtime-stopped'); } },
      { name: '应用资源协议', close: () => { events.push('protocol'); } },
      { name: '应用窗口状态仓储', close: () => { events.push('window-state'); } }
    ]);

    const first = cleanup();
    const second = cleanup();
    expect(first).toBe(second);
    expect(events).toEqual(['workbench']);

    workbenchGate.resolve();
    await first;
    expect(events).toEqual(['workbench', 'runtime-stopped', 'protocol', 'window-state']);
  });

  it('清理步骤失败会输出中文错误但继续后续步骤并允许退出', async () => {
    const errors: string[] = [];
    const events: string[] = [];
    const cleanup = createOrderedCleanup([
      { name: 'Workbench IPC 与应用运行时', close: async () => { throw new Error('停止失败'); } },
      { name: '应用资源协议', close: () => { events.push('protocol'); } },
      { name: '应用窗口状态仓储', close: () => { events.push('window-state'); } }
    ], { error: (message) => errors.push(message) });

    await expect(cleanup()).resolves.toBeUndefined();
    expect(events).toEqual(['protocol', 'window-state']);
    expect(errors).toEqual(['关闭 Workbench IPC 与应用运行时失败：停止失败']);
  });
});

function createController(
  app: FakeApp,
  createMainWindow: () => DesktopMainWindow,
  cleanup: () => Promise<void> = async () => undefined,
  isTrayAvailable: () => boolean = () => true
): WorkbenchLifecycleController {
  return new WorkbenchLifecycleController({ app, createMainWindow, cleanup, isTrayAvailable });
}

class FakeApp extends EventEmitter {
  public quitCalls = 0;
  public constructor(private readonly lock = true) { super(); }
  public requestSingleInstanceLock(): boolean { return this.lock; }
  public quit(): void { this.quitCalls += 1; }
}

class FakeWindow extends EventEmitter implements DesktopMainWindow {
  public closed = false;
  public preventClose = false;
  public closeCalls = 0;
  public destroyCalls = 0;
  public minimized = false;
  public showCalls = 0;
  public hideCalls = 0;
  public focusCalls = 0;
  public readonly events: string[] = [];
  public constructor(private readonly onDestroy: () => void = () => undefined) { super(); }
  public isMinimized(): boolean { return this.minimized; }
  public restore(): void { this.minimized = false; this.events.push('restore'); }
  public show(): void { this.showCalls += 1; this.events.push('show'); }
  public hide(): void { this.hideCalls += 1; this.events.push('hide'); }
  public focus(): void { this.focusCalls += 1; this.events.push('focus'); }
  public close(): void {
    this.closeCalls += 1;
    this.emit('close', { preventDefault: vi.fn() });
    if (this.preventClose) return;
    this.closed = true;
    this.emit('closed');
  }
  public destroy(): void {
    if (this.closed) return;
    this.destroyCalls += 1;
    this.closed = true;
    this.onDestroy();
    this.emit('closed');
  }
}

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>((accept) => { resolve = accept; }), resolve };
}
