export interface IpcHandlerHost {
  handle(channel: string, handler: (...args: any[]) => unknown): void;
  removeHandler(channel: string): void;
}

type IpcHandler<TArgs extends unknown[] = unknown[], TResult = unknown> = (...args: TArgs) => TResult;

/**
 * 跟踪 registerWorkbenchIpc 拥有的 handler 和已经进入的调用。
 * close() 同步封闭入口并移除 handler，随后等待在途调用结算；长链路在创建 runtime 前还会
 * 调用 ensureOpen()，从而阻止 cleanup 开始后跨越 await 再启动新的 Worker。
 */
export class WorkbenchIpcBoundary {
  private readonly channels = new Set<string>();
  private readonly inFlight = new Set<Promise<unknown>>();
  private closing = false;
  private closeOperation: Promise<void> | undefined;

  public constructor(private readonly host: IpcHandlerHost) {}

  public handle<TArgs extends unknown[], TResult>(channel: string, handler: IpcHandler<TArgs, TResult>): void {
    this.host.handle(channel, this.handler(channel, handler));
  }

  /** 返回受跟踪的 handler，供调用方保留 ipcMain.handle 的现有注册形态。 */
  public handler<TArgs extends unknown[], TResult>(channel: string, handler: IpcHandler<TArgs, TResult>): IpcHandler<TArgs, Promise<Awaited<TResult>>> {
    this.channels.add(channel);
    return (...args: TArgs) => {
      try { this.ensureOpen(); } catch (error) { return Promise.reject(error); }
      let operation: Promise<Awaited<TResult>>;
      try { operation = Promise.resolve(handler(...args)) as Promise<Awaited<TResult>>; }
      catch (error) { operation = Promise.reject(error); }
      this.inFlight.add(operation);
      const clear = () => { this.inFlight.delete(operation); };
      void operation.then(clear, clear);
      return operation;
    };
  }

  public ensureOpen(): void {
    if (this.closing) throw new Error('Workbench 正在退出，不能继续处理 IPC 请求');
  }

  public close(): Promise<void> {
    if (this.closeOperation) return this.closeOperation;
    this.closing = true;
    for (const channel of this.channels) this.host.removeHandler(channel);
    this.channels.clear();
    this.closeOperation = Promise.allSettled([...this.inFlight]).then(() => undefined);
    return this.closeOperation;
  }
}

interface WorkbenchIpcCleanupOptions {
  ipcBoundary: Pick<WorkbenchIpcBoundary, 'close'>;
  unregisterWindowShellIpc(): void;
  unregisterRuntimeEvents(): void;
  waitForInitialization(): Promise<void>;
  stopAllRuntimes(): Promise<void>;
  closeAppRegistry(): void;
  closeDesktopRepository(): void;
}

/** 同步封闭 IPC，drain 在途调用后停止全部 runtime，最后才关闭宿主仓储。 */
export function createWorkbenchIpcCleanup(options: WorkbenchIpcCleanupOptions): () => Promise<void> {
  let cleanupOperation: Promise<void> | undefined;
  return () => {
    if (cleanupOperation) return cleanupOperation;
    const failures: string[] = [];
    const ipcDrain = options.ipcBoundary.close();
    try { options.unregisterWindowShellIpc(); } catch (error) { failures.push(`注销窗口 IPC 失败：${errorMessage(error)}`); }
    try { options.unregisterRuntimeEvents(); } catch (error) { failures.push(`注销 runtime 监听失败：${errorMessage(error)}`); }
    cleanupOperation = (async () => {
      try { await ipcDrain; } catch (error) { failures.push(`等待 IPC 调用结束失败：${errorMessage(error)}`); }
      try { await options.waitForInitialization(); } catch (error) { failures.push(`等待预置应用初始化失败：${errorMessage(error)}`); }
      try { await options.stopAllRuntimes(); } catch (error) { failures.push(errorMessage(error)); }
      try { options.closeAppRegistry(); } catch (error) { failures.push(`关闭应用注册表失败：${errorMessage(error)}`); }
      try { options.closeDesktopRepository(); } catch (error) { failures.push(`关闭桌面布局仓储失败：${errorMessage(error)}`); }
      if (failures.length > 0) throw new Error(`关闭 Workbench IPC 失败：${failures.join('；')}`);
    })();
    return cleanupOperation;
  };
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
