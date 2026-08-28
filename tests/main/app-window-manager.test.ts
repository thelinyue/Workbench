import { EventEmitter } from 'node:events';
import { describe, expect, it, vi } from 'vitest';
import { AppWindowManager, type AppWindowCreationOptions, type AppWindowHost, type AppWindowStateStore } from '../../src/main/services/app-window-manager';
import { WorkbenchLifecycleController, createOrderedCleanup, type DesktopMainWindow } from '../../src/main/services/workbench-lifecycle';
import type { AppWindowManifest } from '../../src/shared/app-contract';

const windowManifest: AppWindowManifest = {
  defaultSize: { width: 1200, height: 800 },
  minSize: { width: 800, height: 560 }
};

describe('应用窗口管理器', () => {
  it('相同应用和默认 main 键复用窗口并聚焦', async () => {
    const fixture = createFixture();

    const first = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });
    const second = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(second).toBe(first);
    expect(fixture.windows).toHaveLength(1);
    expect(fixture.windows[0]?.shown).toBe(true);
    expect(fixture.windows[0]?.focused).toBe(true);
    expect(fixture.manager.resolveWebContents(fixture.windows[0]!.webContents.id)).toEqual({ appId: 'analysis-center', windowKey: 'main' });
  });

  it('同一应用的不同 windowKey 创建独立窗口', async () => {
    const fixture = createFixture();

    await fixture.manager.open({ appId: 'analysis-center', windowKey: 'main', name: '分析中心', window: windowManifest });
    await fixture.manager.open({ appId: 'analysis-center', windowKey: 'evidence', name: '分析中心', window: windowManifest });

    expect(fixture.windows).toHaveLength(2);
    expect(fixture.manager.resolveWebContents(fixture.windows[1]!.webContents.id)).toEqual({ appId: 'analysis-center', windowKey: 'evidence' });
  });

  it('复用最小化窗口时先恢复再显示和聚焦', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    window.minimized = true;

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(window.actions.slice(-3)).toEqual(['restore', 'show', 'focus']);
  });

  it('通知激活事件在应用表面就绪前排队，就绪后只投递到同一 appId/windowKey', async () => {
    const fixture = createFixture();
    const main = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    const evidence = await fixture.manager.open({ appId: 'analysis-center', windowKey: 'evidence', name: '分析中心', window: windowManifest }) as FakeWindow;
    const event = { appId: 'analysis-center', event: 'host.notification.activated', payload: { packageId: 'package-1' } };

    fixture.manager.deliverEvent('analysis-center', 'main', event);
    expect(main.sent).toEqual([]);
    expect(evidence.sent).toEqual([]);

    fixture.manager.markEventSurfaceReady(main.webContents.id);

    expect(main.sent).toEqual([{ channel: 'workbench:app-event', value: event }]);
    expect(evidence.sent).toEqual([]);
  });

  it('应用表面已就绪时立即投递激活事件，关闭窗口后丢弃未交付事件', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    fixture.manager.markEventSurfaceReady(window.webContents.id);
    const event = { appId: 'analysis-center', event: 'host.notification.activated', payload: { packageId: 'package-2' } };

    fixture.manager.deliverEvent('analysis-center', 'main', event);
    window.close();
    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });
    fixture.manager.markEventSurfaceReady(fixture.windows[1]!.webContents.id);

    expect(window.sent).toEqual([{ channel: 'workbench:app-event', value: event }]);
    expect(fixture.windows[1]!.sent).toEqual([]);
  });

  it('关闭时保存普通边界和最大化状态，closed 后清理两个身份映射', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    const webContentsId = window.webContents.id;
    window.normalBounds = { x: 140, y: 90, width: 1180, height: 760 };
    window.maximized = true;

    window.close();
    expect(fixture.savedStates).toEqual([{
      appId: 'analysis-center', windowKey: 'main', x: 140, y: 90, width: 1180, height: 760, maximized: true
    }]);

    expect(() => window.webContents).toThrow('Object has been destroyed');
    expect(fixture.manager.resolveWebContents(webContentsId)).toBeUndefined();
    const replacement = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });
    expect(replacement).not.toBe(window);
    expect(fixture.windows).toHaveLength(2);
  });

  it('第一次 close 被取消后，后续真实 close 仍保存最新状态并移除监听', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    window.preventClose = true;
    window.normalBounds = { x: 10, y: 20, width: 1000, height: 700 };

    window.close();
    expect(window.closed).toBe(false);

    window.preventClose = false;
    window.normalBounds = { x: 30, y: 40, width: 1100, height: 720 };
    window.close();

    expect(fixture.savedStates).toEqual([
      { appId: 'analysis-center', windowKey: 'main', x: 10, y: 20, width: 1000, height: 700, maximized: false },
      { appId: 'analysis-center', windowKey: 'main', x: 30, y: 40, width: 1100, height: 720, maximized: false }
    ]);
    expect(window.listenerCount('close')).toBe(0);
  });

  it('普通 close 的状态仓储异常记录中文错误且不阻断窗口关闭', async () => {
    const errors: string[] = [];
    const fixture = createFixture(undefined, undefined, {
      upsert: () => { throw new Error('磁盘写入失败'); }
    }, { error: (message) => errors.push(message) });
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;

    expect(() => window.close()).not.toThrow();

    expect(window.closed).toBe(true);
    expect(errors).toEqual(['保存应用窗口状态失败（analysis-center/main）：磁盘写入失败']);
  });

  it('恢复保存状态时不小于 manifest 最小尺寸并在创建后恢复最大化', async () => {
    const fixture = createFixture({
      appId: 'analysis-center', windowKey: 'main', x: 100, y: 80, width: 640, height: 480, maximized: true
    });

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.createdOptions[0]).toMatchObject({ x: 100, y: 80, width: 800, height: 560, minWidth: 800, minHeight: 560 });
    expect(fixture.windows[0]?.maximized).toBe(true);
  });

  it('保存位置完全离屏时丢弃旧尺寸并在主屏工作区居中默认尺寸', async () => {
    const fixture = createFixture({
      appId: 'analysis-center', windowKey: 'main', x: 5000, y: 5000, width: 1600, height: 1000, maximized: false
    });

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.createdOptions[0]).toMatchObject({ x: 360, y: 140, width: 1200, height: 800 });
  });

  it('主屏小于 manifest 默认尺寸时按工作区裁剪并仍满足最小尺寸', async () => {
    const fixture = createFixture(undefined, {
      rendererUrl: 'http://localhost:5173',
      workArea: { x: 0, y: 0, width: 1024, height: 700 }
    });

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.createdOptions[0]).toMatchObject({ x: 0, y: 0, width: 1024, height: 700, minWidth: 800, minHeight: 560 });
  });

  it('保存区域与屏幕相交时裁剪尺寸和坐标，使窗口完整位于工作区', async () => {
    const fixture = createFixture({
      appId: 'analysis-center', windowKey: 'main', x: 1700, y: 900, width: 1400, height: 900, maximized: false
    });

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.createdOptions[0]).toMatchObject({ x: 520, y: 180, width: 1400, height: 900 });
  });

  it('创建安全的独立无框窗口并只向 Workbench renderer 查询 surface', async () => {
    const fixture = createFixture();

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.createdOptions[0]).toMatchObject({
      frame: false, modal: false, skipTaskbar: false, show: false,
      webPreferences: { preload: 'D:/workbench/preload.js', contextIsolation: true, nodeIntegration: false, sandbox: true }
    });
    expect(fixture.createdOptions[0]).not.toHaveProperty('parent');
    expect(fixture.windows[0]?.loadedUrl).toBe('http://localhost:5173/?surface=app-window');
    expect(fixture.windows[0]?.loadedUrl).not.toContain('analysis-center');
  });

  it('拒绝所有 popup，并阻止 iframe 通过 _top 导航离开可信 renderer', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;

    expect(window.requestWindowOpen('https://attacker.example/popup')).toEqual({ action: 'deny' });

    const externalMainFrame = window.requestFrameNavigation('https://attacker.example/replaced', true);
    const expectedReload = window.requestFrameNavigation('http://localhost:5173/?surface=app-window', true);
    const hostedAppChildFrame = window.requestFrameNavigation('workbench-app://analysis-center/2.0.0/index.html', false);

    expect(externalMainFrame).toHaveBeenCalledOnce();
    expect(expectedReload).not.toHaveBeenCalled();
    expect(hostedAppChildFrame).not.toHaveBeenCalled();
  });

  it('打包版 renderer 通过 loadFile 只携带 surface 查询参数', async () => {
    const fixture = createFixture(undefined, { rendererUrl: undefined, rendererFile: 'D:/workbench/renderer/index.html' });

    await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.windows[0]?.loadedFile).toEqual({
      path: 'D:/workbench/renderer/index.html', options: { query: { surface: 'app-window' } }
    });
    expect(fixture.windows[0]?.requestFrameNavigation('file:///D:/workbench/renderer/index.html?surface=app-window', true))
      .not.toHaveBeenCalled();
  });

  it('renderer 加载失败时向调用方报错、清理身份并销毁隐藏窗口且不保存状态', async () => {
    const fixture = createFixture(undefined, {
      rendererUrl: 'http://localhost:5173',
      loadBehaviors: [() => Promise.reject(new Error('连接被拒绝'))]
    });

    await expect(Promise.resolve(fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest })))
      .rejects.toThrow('应用窗口加载 Workbench renderer 失败：连接被拒绝');

    expect(fixture.windows[0]?.destroyed).toBe(true);
    expect(fixture.windows[0]?.shown).toBe(false);
    expect(() => fixture.windows[0]!.webContents).toThrow('Object has been destroyed');
    expect(fixture.manager.resolveWebContents(fixture.windows[0]!.webContentsId)).toBeUndefined();
    expect(fixture.savedStates).toEqual([]);
  });

  it('renderer 加载失败后同键再次打开会创建新窗口并重试', async () => {
    const fixture = createFixture(undefined, {
      rendererUrl: 'http://localhost:5173',
      loadBehaviors: [() => Promise.reject(new Error('首次失败')), () => Promise.resolve()]
    });

    await expect(Promise.resolve(fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }))).rejects.toThrow('首次失败');
    const retried = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });

    expect(fixture.windows).toHaveLength(2);
    expect(retried).toBe(fixture.windows[1]);
    expect(fixture.manager.resolveWebContents(fixture.windows[1]!.webContents.id)).toEqual({ appId: 'analysis-center', windowKey: 'main' });
  });

  it('并发打开同一键时等待同一个加载过程且只创建一个窗口', async () => {
    const loading = deferred<void>();
    const fixture = createFixture(undefined, {
      rendererUrl: 'http://localhost:5173',
      loadBehaviors: [() => loading.promise]
    });

    const first = fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });
    const second = fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest });
    let settled = false;
    void Promise.resolve(first).then(() => { settled = true; });
    await Promise.resolve();

    expect(fixture.windows).toHaveLength(1);
    expect(settled).toBe(false);
    loading.resolve();
    await expect(Promise.all([first, second])).resolves.toEqual([fixture.windows[0], fixture.windows[0]]);
  });

  it('最终退出不依赖可取消的常规 close，仍会及时强制销毁窗口', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    window.preventClose = true;

    window.close();
    expect(window.closed).toBe(false);

    const closing = fixture.manager.closeAll();
    const outcome = await Promise.race([
      closing.then(() => 'settled' as const),
      new Promise<'pending'>((resolve) => setImmediate(() => resolve('pending')))
    ]);

    expect(outcome).toBe('settled');
    expect(window.destroyCalls).toBe(1);
    expect(window.destroyed).toBe(true);
  });

  it('最终退出显式保存一次状态并抑制普通 close listener 重复写入', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    window.normalBounds = { x: 140, y: 90, width: 1180, height: 760 };
    window.maximized = true;

    await fixture.manager.closeAll();
    window.emit('close');

    expect(fixture.savedStates).toEqual([{
      appId: 'analysis-center', windowKey: 'main', x: 140, y: 90, width: 1180, height: 760, maximized: true
    }]);
    expect(window.closeCalls).toBe(0);
    expect(window.destroyCalls).toBe(1);
  });

  it('单个窗口保存或销毁失败时继续强制销毁其他窗口并聚合中文错误', async () => {
    const fixture = createFixture(undefined, { rendererUrl: 'http://localhost:5173' }, {
      upsert: (state) => {
        if (state.appId === 'save-fails') throw new Error('磁盘写入失败');
      }
    });
    const saveFailure = await fixture.manager.open({ appId: 'save-fails', name: '保存失败', window: windowManifest }) as FakeWindow;
    const destroyFailure = await fixture.manager.open({ appId: 'destroy-fails', name: '销毁失败', window: windowManifest }) as FakeWindow;
    const healthy = await fixture.manager.open({ appId: 'healthy', name: '正常窗口', window: windowManifest }) as FakeWindow;
    destroyFailure.destroyError = new Error('宿主销毁失败');

    const failure = await fixture.manager.closeAll().then(() => undefined, (error: unknown) => error);

    expect(failure).toBeInstanceOf(Error);
    expect((failure as Error).message).toContain('保存应用窗口状态失败（save-fails/main）：磁盘写入失败');
    expect((failure as Error).message).toContain('强制销毁应用窗口失败（destroy-fails/main）：宿主销毁失败');
    expect(saveFailure.destroyed).toBe(true);
    expect(destroyFailure.destroyCalls).toBe(1);
    expect(healthy.destroyed).toBe(true);
    expect(fixture.manager.resolveWebContents(saveFailure.webContentsId)).toBeUndefined();
    expect(fixture.manager.resolveWebContents(destroyFailure.webContentsId)).toBeUndefined();
    expect(fixture.manager.resolveWebContents(healthy.webContentsId)).toBeUndefined();
  });

  it('destroy 抛错的幸存窗口在仓储关闭后不再由 close listener 写入状态', async () => {
    let repositoryClosed = false;
    let writesAfterClose = 0;
    const fixture = createFixture(undefined, { rendererUrl: 'http://localhost:5173' }, {
      upsert: () => { if (repositoryClosed) writesAfterClose += 1; }
    });
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    window.destroyError = new Error('窗口仍然存活');

    await expect(fixture.manager.closeAll()).rejects.toThrow('强制销毁应用窗口失败');
    repositoryClosed = true;
    window.emit('close');

    expect(writesAfterClose).toBe(0);
  });

  it('并发 closeAll 共享 Promise 且只强制销毁一次', async () => {
    const fixture = createFixture();
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;

    const first = fixture.manager.closeAll();
    const second = fixture.manager.closeAll();

    expect(second).toBe(first);
    await first;
    expect(window.destroyCalls).toBe(1);
  });

  it('退出时先关闭 App Window 并保存状态，再关闭仓储，最终 quit 不会关库后写入', async () => {
    const events: string[] = [];
    let repositoryClosed = false;
    let writesAfterClose = 0;
    const fixture = createFixture(undefined, { rendererUrl: 'http://localhost:5173' }, {
      upsert: () => {
        if (repositoryClosed) writesAfterClose += 1;
        events.push('state-saved');
      }
    });
    const window = await fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest }) as FakeWindow;
    const cleanup = createOrderedCleanup([
      { name: '应用窗口', close: () => fixture.manager.closeAll() },
      { name: '应用窗口状态仓储', close: () => { repositoryClosed = true; events.push('repository-closed'); } }
    ]);
    const app = new FakeLifecycleApp(() => events.push('final-quit'));
    new WorkbenchLifecycleController({
      app,
      createMainWindow: () => new FakeDesktopMainWindow(),
      getNativeWindowCount: () => 1,
      cleanup,
      platform: 'win32'
    });
    const quitEvent = { preventDefault: vi.fn() };

    app.emit('before-quit', quitEvent);
    await vi.waitFor(() => expect(app.quitCalls).toBe(1));

    expect(quitEvent.preventDefault).toHaveBeenCalledOnce();
    expect(window.destroyCalls).toBe(1);
    expect(events).toEqual(['state-saved', 'repository-closed', 'final-quit']);
    window.close();
    expect(writesAfterClose).toBe(0);
    await expect(fixture.manager.open({ appId: 'analysis-center', name: '分析中心', window: windowManifest })).rejects.toThrow('应用窗口管理器正在关闭');
  });
});

function createFixture(
  restoredState?: Parameters<AppWindowStateStore['upsert']>[0],
  renderer: {
    rendererUrl?: string;
    rendererFile?: string;
    workArea?: { x: number; y: number; width: number; height: number };
    loadBehaviors?: Array<() => Promise<void>>;
  } = { rendererUrl: 'http://localhost:5173/?old=value' },
  stateStoreOverride: Partial<AppWindowStateStore> = {},
  logger?: Pick<Console, 'error'>
) {
  const windows: FakeWindow[] = [];
  const createdOptions: AppWindowCreationOptions[] = [];
  const savedStates: Array<Parameters<AppWindowStateStore['upsert']>[0]> = [];
  let nextId = 1;
  const stateStore: AppWindowStateStore = {
    load: (appId, windowKey) => restoredState?.appId === appId && restoredState.windowKey === windowKey ? restoredState : undefined,
    upsert: (state) => { savedStates.push(state); },
    ...stateStoreOverride
  };
  const manager = new AppWindowManager({
    stateStore,
    createWindow: (options) => {
      createdOptions.push(options);
      const window = new FakeWindow(nextId++, options, renderer.loadBehaviors?.shift());
      windows.push(window);
      return window;
    },
    getDisplays: () => [{ workArea: renderer.workArea ?? { x: 0, y: 0, width: 1920, height: 1080 } }],
    getPrimaryDisplay: () => ({ workArea: renderer.workArea ?? { x: 0, y: 0, width: 1920, height: 1080 } }),
    preloadPath: 'D:/workbench/preload.js',
    rendererUrl: renderer.rendererUrl,
    rendererFile: renderer.rendererFile,
    logger
  });
  return { manager, windows, createdOptions, savedStates };
}

class FakeWindow extends EventEmitter implements AppWindowHost {
  private readonly webContentsValue: {
    id: number;
    send(channel: string, value: unknown): void;
    setWindowOpenHandler(handler: (details: { url: string }) => { action: 'deny' }): void;
    on(event: 'will-frame-navigate', listener: (event: { url: string; isMainFrame: boolean; preventDefault(): void }) => void): void;
  };
  private windowOpenHandler?: (details: { url: string }) => { action: 'deny' };
  private frameNavigationHandler?: (event: { url: string; isMainFrame: boolean; preventDefault(): void }) => void;
  public readonly webContentsId: number;
  public minimized = false;
  public maximized = false;
  public shown = false;
  public focused = false;
  public destroyed = false;
  public closed = false;
  public preventClose = false;
  public destroyError?: Error;
  public closeCalls = 0;
  public destroyCalls = 0;
  public actions: string[] = [];
  public sent: Array<{ channel: string; value: unknown }> = [];
  public loadedUrl?: string;
  public loadedFile?: { path: string; options?: { query?: Record<string, string> } };
  public normalBounds: { x: number; y: number; width: number; height: number };

  public constructor(
    id: number,
    options: { x?: number; y?: number; width: number; height: number },
    private readonly loadBehavior: () => Promise<void> = () => Promise.resolve()
  ) {
    super();
    this.webContentsId = id;
    this.webContentsValue = {
      id,
      send: (channel, value) => { this.sent.push({ channel, value }); },
      setWindowOpenHandler: (handler) => { this.windowOpenHandler = handler; },
      on: (_event, listener) => { this.frameNavigationHandler = listener; }
    };
    this.normalBounds = { x: options.x ?? 0, y: options.y ?? 0, width: options.width, height: options.height };
  }

  public get webContents(): AppWindowHost['webContents'] {
    if (this.closed || this.destroyed) throw new TypeError('Object has been destroyed');
    return this.webContentsValue;
  }

  public requestWindowOpen(url: string): { action: 'deny' } | undefined {
    return this.windowOpenHandler?.({ url });
  }

  public requestFrameNavigation(url: string, isMainFrame: boolean) {
    const preventDefault = vi.fn();
    this.frameNavigationHandler?.({ url, isMainFrame, preventDefault });
    return preventDefault;
  }

  public isMinimized(): boolean { return this.minimized; }
  public restore(): void { this.minimized = false; this.actions.push('restore'); }
  public show(): void { this.shown = true; this.actions.push('show'); }
  public focus(): void { this.focused = true; this.actions.push('focus'); }
  public getNormalBounds() { return this.normalBounds; }
  public isMaximized(): boolean { return this.maximized; }
  public maximize(): void { this.maximized = true; this.actions.push('maximize'); }
  public loadURL(url: string): Promise<void> { this.loadedUrl = url; return this.loadBehavior(); }
  public loadFile(path: string, options?: { query?: Record<string, string> }): Promise<void> { this.loadedFile = { path, options }; return this.loadBehavior(); }
  public close(): void {
    if (this.closed) return;
    this.closeCalls += 1;
    this.emit('close');
    if (this.preventClose) return;
    this.closed = true;
    this.emit('closed');
  }
  public destroy(): void {
    this.destroyCalls += 1;
    if (this.destroyError) throw this.destroyError;
    this.destroyed = true;
    if (this.closed) return;
    this.closed = true;
    this.emit('closed');
  }
}

class FakeLifecycleApp extends EventEmitter {
  public quitCalls = 0;
  public constructor(private readonly onQuit: () => void) { super(); }
  public requestSingleInstanceLock(): boolean { return true; }
  public quit(): void { this.quitCalls += 1; this.onQuit(); }
}

class FakeDesktopMainWindow extends EventEmitter implements DesktopMainWindow {
  public isMinimized(): boolean { return false; }
  public restore(): void {}
  public show(): void {}
  public focus(): void {}
  public destroy(): void { this.emit('closed'); }
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}
