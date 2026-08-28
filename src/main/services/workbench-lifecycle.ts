export interface DesktopMainWindow {
  isMinimized(): boolean;
  restore(): void;
  show(): void;
  focus(): void;
  destroy(): void;
  once(event: 'closed', listener: () => void): this;
}

interface LifecycleApp {
  requestSingleInstanceLock(): boolean;
  quit(): void;
  on(event: 'second-instance' | 'activate' | 'window-all-closed', listener: () => void): this;
  on(event: 'before-quit', listener: (event: { preventDefault(): void }) => void): this;
}

interface WorkbenchLifecycleOptions {
  app: LifecycleApp;
  createMainWindow(): DesktopMainWindow;
  getNativeWindowCount(): number;
  cleanup(): Promise<void>;
  platform: NodeJS.Platform;
  logger?: Pick<Console, 'error'>;
}

interface CleanupStep {
  name: string;
  close(): Promise<void> | void;
}

/** 第二个实例只负责通知首实例；拿不到锁的进程不再注册窗口和仓储生命周期。 */
export function acquireSingleInstance(app: Pick<LifecycleApp, 'requestSingleInstanceLock' | 'quit'>): boolean {
  const acquired = app.requestSingleInstanceLock();
  if (!acquired) app.quit();
  return acquired;
}

/**
 * 将宿主资源按声明顺序关闭，并让所有并发调用共享同一个 Promise。
 * 单步失败会输出部署者可读的中文错误，但不会阻止后续资源收口或最终退出。
 */
export function createOrderedCleanup(steps: CleanupStep[], logger: Pick<Console, 'error'> = console): () => Promise<void> {
  let cleanupOperation: Promise<void> | undefined;
  return () => {
    if (cleanupOperation) return cleanupOperation;
    cleanupOperation = (async () => {
      for (const step of steps) {
        try { await step.close(); }
        catch (error) { logger.error(`关闭 ${step.name}失败：${errorMessage(error)}`); }
      }
    })();
    return cleanupOperation;
  };
}

/**
 * Workbench 原生窗口与进程生命周期控制器。
 *
 * mainWindow 只跟踪桌面主窗口，不读取 AppWindowManager 的业务窗口集合；因此主窗口关闭时
 * App Window 可以继续承载后台任务。second-instance 与 activate 始终走同一恢复/重建路径。
 * before-quit 使用单一异步 gate：等待期的每次退出都被阻止，清理完成后再次 app.quit，随后
 * 由 shutdownComplete 放行 Electron 的最终退出事件。
 */
export class WorkbenchLifecycleController {
  private mainWindow: DesktopMainWindow | undefined;
  private cleanupOperation: Promise<void> | undefined;
  private shutdownComplete = false;

  public constructor(private readonly options: WorkbenchLifecycleOptions) {
    options.app.on('second-instance', () => { this.restoreOrCreateMainWindow(); });
    options.app.on('activate', () => { this.restoreOrCreateMainWindow(); });
    options.app.on('window-all-closed', () => {
      if (options.platform !== 'darwin' && options.getNativeWindowCount() === 0) options.app.quit();
    });
    options.app.on('before-quit', (event) => { this.beforeQuit(event); });
  }

  public openMainWindow(): DesktopMainWindow {
    if (this.mainWindow) return this.mainWindow;
    const window = this.options.createMainWindow();
    this.mainWindow = window;
    window.once('closed', () => { if (this.mainWindow === window) this.mainWindow = undefined; });
    return window;
  }

  /**
   * 仅供最终退出清理使用：先清除跟踪引用，再强制销毁主窗口，避免 renderer beforeunload
   * 取消普通 close 后留下一个已经失去 runtime、IPC 和协议资源的进程。
   */
  public destroyMainWindowForShutdown(): void {
    const window = this.mainWindow;
    if (!window) return;
    this.mainWindow = undefined;
    window.destroy();
  }

  private restoreOrCreateMainWindow(): void {
    if (!this.mainWindow) { this.openMainWindow(); return; }
    if (this.mainWindow.isMinimized()) this.mainWindow.restore();
    this.mainWindow.show();
    this.mainWindow.focus();
  }

  private beforeQuit(event: { preventDefault(): void }): void {
    if (this.shutdownComplete) return;
    event.preventDefault();
    if (this.cleanupOperation) return;
    try {
      this.cleanupOperation = this.options.cleanup();
    } catch (error) {
      this.cleanupOperation = Promise.reject(error);
    }
    void this.cleanupOperation.then(
      () => this.finishShutdown(),
      (error) => {
        (this.options.logger ?? console).error(`Workbench 异步清理失败：${errorMessage(error)}`);
        this.finishShutdown();
      }
    );
  }

  private finishShutdown(): void {
    this.shutdownComplete = true;
    this.options.app.quit();
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
