import { randomUUID } from 'node:crypto';
import { Worker } from 'node:worker_threads';
import { join } from 'node:path';
import { EventEmitter } from 'node:events';
import type { AppHostEvent, AppManifestV1 } from '../../shared/app-contract';

interface AppRuntimeStartOptions {
  appId: string;
  installPath: string;
  dataDirectory: string;
  manifest: AppManifestV1;
}

interface AppWorkerResponse {
  type: 'response';
  requestId: string;
  ok: boolean;
  result?: unknown;
  errorMessage?: string;
}

interface AppWorkerEvent {
  type: 'event';
  appId: string;
  event: string;
  payload: unknown;
}

interface AppWorkerStopped {
  type: 'stopped';
  ok: boolean;
  errorMessage?: string;
}

export interface AppRuntimeWorker {
  postMessage(message: unknown): void;
  terminate(): Promise<number>;
  on(event: 'message', listener: (message: unknown) => void): this;
  once(event: 'error', listener: (error: Error) => void): this;
  once(event: 'exit', listener: (code: number) => void): this;
  off(event: 'message', listener: (message: unknown) => void): this;
  off(event: 'error', listener: (error: Error) => void): this;
  off(event: 'exit', listener: (code: number) => void): this;
}

interface RuntimeRecord {
  options: AppRuntimeStartOptions;
  worker?: AppRuntimeWorker;
  pending: Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>;
}

export interface AppRuntimeManagerOptions {
  createWorker?: (workerData: Record<string, unknown>) => AppRuntimeWorker;
  stopTimeoutMs?: number;
  logger?: Pick<Console, 'error'>;
}

const DEFAULT_STOP_TIMEOUT_MS = 5_000;

/**
 * 管理每个应用的独立 backend Worker 和停止握手。
 *
 * 停止开始后先从可调用集合移除 runtime，并拒绝全部待处理 RPC；同一 appId 的并发停止
 * 共享 stopOperations 中的同一个 Promise。Worker 有固定 5 秒机会完成业务清理并确认，只有
 * 超时才调用 terminate，避免 SQLite 等应用私有资源在异步关闭途中被宿主强行截断。
 */
export class AppRuntimeManager {
  private readonly runtimes = new Map<string, RuntimeRecord>();
  private readonly stopOperations = new Map<string, Promise<void>>();
  private readonly events = new EventEmitter();

  public constructor(private readonly options: AppRuntimeManagerOptions = {}) {}

  public async start(startOptions: AppRuntimeStartOptions): Promise<void> {
    if (this.runtimes.has(startOptions.appId)) return;
    if (startOptions.manifest.runtime.kind === 'web') {
      this.runtimes.set(startOptions.appId, { options: startOptions, pending: new Map() });
      return;
    }
    const worker = (this.options.createWorker ?? createDefaultWorker)({
      appId: startOptions.appId,
      backendEntry: join(startOptions.installPath, startOptions.manifest.runtime.backendEntry),
      dataDirectory: startOptions.dataDirectory,
      manifest: startOptions.manifest
    });
    const runtime: RuntimeRecord = { options: startOptions, worker, pending: new Map() };
    this.runtimes.set(startOptions.appId, runtime);
    worker.on('message', (message) => this.handleMessage(startOptions.appId, message));
    worker.once('error', (error) => this.failRuntime(startOptions.appId, new Error(`应用 Worker 异常：${error.message}`)));
    worker.once('exit', (code) => { if (code !== 0) this.failRuntime(startOptions.appId, new Error(`应用 Worker 异常退出，退出码：${code}`)); });
  }

  public invoke(appId: string, method: string, payload: unknown): Promise<unknown> {
    const runtime = this.runtimes.get(appId);
    if (!runtime) return Promise.reject(new Error(`应用尚未启动：${appId}`));
    const worker = runtime.worker;
    if (!worker) return Promise.reject(new Error(`应用不支持 backend：${appId}`));
    const requestId = randomUUID();
    return new Promise((resolve, reject) => {
      runtime.pending.set(requestId, { resolve, reject });
      worker.postMessage({ type: 'invoke', requestId, method, payload });
    });
  }

  public stop(appId: string): Promise<void> {
    const existing = this.stopOperations.get(appId);
    if (existing) return existing;
    const runtime = this.runtimes.get(appId);
    if (!runtime) return Promise.resolve();
    this.runtimes.delete(appId);
    this.rejectPending(runtime, new Error(`应用正在停止：${appId}`));
    const operation = runtime.worker ? this.stopWorker(appId, runtime.worker) : Promise.resolve();
    this.stopOperations.set(appId, operation);
    const clear = () => { if (this.stopOperations.get(appId) === operation) this.stopOperations.delete(appId); };
    void operation.then(clear, clear);
    return operation;
  }

  /** 等待所有已启动或正在停止的 runtime；单个失败不会阻止其余应用收到 stop。 */
  public async stopAll(): Promise<void> {
    const appIds = [...new Set([...this.runtimes.keys(), ...this.stopOperations.keys()])];
    const results = await Promise.allSettled(appIds.map((appId) => this.stop(appId)));
    const failures = results.flatMap((result, index) => result.status === 'rejected'
      ? [`${appIds[index]}：${errorMessage(result.reason)}`]
      : []);
    if (failures.length > 0) throw new Error(`停止应用运行时失败：${failures.join('；')}`);
  }

  /** 开发重载复用正常生命周期，确保新 renderer 不会继续向旧 backend 发送 RPC。 */
  public async restart(startOptions: AppRuntimeStartOptions): Promise<void> {
    await this.stop(startOptions.appId);
    await this.start(startOptions);
  }

  public onEvent(listener: (event: AppHostEvent) => void): () => void {
    this.events.on('event', listener);
    return () => this.events.off('event', listener);
  }

  private handleMessage(appId: string, message: unknown): void {
    const runtime = this.runtimes.get(appId);
    if (!runtime || !message || typeof message !== 'object') return;
    const value = message as Partial<AppWorkerResponse> & Partial<AppWorkerEvent>;
    if (value.type === 'response' && value.requestId) {
      const pending = runtime.pending.get(value.requestId);
      if (!pending) return;
      runtime.pending.delete(value.requestId);
      value.ok ? pending.resolve(value.result) : pending.reject(new Error(value.errorMessage ?? '应用请求失败'));
      return;
    }
    if (value.type === 'event' && value.event) this.events.emit('event', { appId, event: value.event, payload: value.payload } satisfies AppHostEvent);
  }

  private failRuntime(appId: string, error: Error): void {
    const runtime = this.runtimes.get(appId);
    if (!runtime) return;
    this.runtimes.delete(appId);
    this.rejectPending(runtime, error);
    this.events.emit('event', { appId, event: 'runtime.failed', payload: { message: error.message } } satisfies AppHostEvent);
  }

  private rejectPending(runtime: RuntimeRecord, error: Error): void {
    for (const pending of runtime.pending.values()) pending.reject(error);
    runtime.pending.clear();
  }

  private stopWorker(appId: string, worker: AppRuntimeWorker): Promise<void> {
    const timeoutMs = this.options.stopTimeoutMs ?? DEFAULT_STOP_TIMEOUT_MS;
    const logger = this.options.logger ?? console;
    return new Promise<void>((resolve, reject) => {
      let settled = false;
      let timeout: NodeJS.Timeout | undefined;
      const cleanupListeners = () => {
        if (timeout) clearTimeout(timeout);
        worker.off('message', onMessage);
        worker.off('error', onError);
        worker.off('exit', onExit);
      };
      const settle = (complete: () => void) => {
        if (settled) return;
        settled = true;
        cleanupListeners();
        complete();
      };
      const onMessage = (message: unknown) => {
        if (!isStoppedMessage(message)) return;
        settle(() => message.ok
          ? resolve()
          : reject(new Error(`应用 ${appId} 清理失败：${message.errorMessage ?? 'backend 未提供失败原因'}`)));
      };
      const onError = (error: Error) => settle(() => reject(new Error(`应用 ${appId} Worker 异常：${error.message}`)));
      const onExit = (code: number) => settle(() => reject(new Error(`应用 ${appId} Worker 在清理确认前退出，退出码：${code}`)));

      worker.on('message', onMessage);
      worker.once('error', onError);
      worker.once('exit', onExit);
      timeout = setTimeout(() => {
        settle(() => {
          logger.error(`应用 ${appId} 在 ${timeoutMs}ms 内未完成清理，正在强制终止 Worker。`);
          void worker.terminate().then(
            () => reject(new Error(`等待应用 ${appId} 清理确认超时（${timeoutMs}ms）`)),
            (error) => reject(new Error(`等待应用 ${appId} 清理确认超时，强制终止 Worker 也失败：${errorMessage(error)}`))
          );
        });
      }, timeoutMs);
      try {
        worker.postMessage({ type: 'stop' });
      } catch (error) {
        settle(() => reject(new Error(`无法通知应用 ${appId} 开始清理：${errorMessage(error)}`)));
      }
    });
  }
}

function isStoppedMessage(message: unknown): message is AppWorkerStopped {
  if (!message || typeof message !== 'object') return false;
  const value = message as Partial<AppWorkerStopped>;
  return value.type === 'stopped' && typeof value.ok === 'boolean';
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function createDefaultWorker(workerData: Record<string, unknown>): AppRuntimeWorker {
  return new Worker(join(__dirname, 'app-backend-worker.js'), { workerData });
}
