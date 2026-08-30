import { randomUUID } from 'node:crypto';
import { Worker } from 'node:worker_threads';
import { join } from 'node:path';
import { EventEmitter } from 'node:events';
import type { AppHostEvent, AppManifestV1, AppRuntimeState } from '../../shared/app-contract';

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

interface AppWorkerReady {
  type: 'ready';
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

interface AppWorkerNotification {
  type: 'notification';
  payload: unknown;
}

export interface AppRuntimeNotification {
  appId: string;
  payload: unknown;
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
  cancelStart?: (error: Error) => void;
}

export interface AppRuntimeManagerOptions {
  createWorker?: (workerData: Record<string, unknown>) => AppRuntimeWorker;
  startTimeoutMs?: number;
  stopTimeoutMs?: number;
  logger?: Pick<Console, 'error'>;
}

const DEFAULT_START_TIMEOUT_MS = 10_000;
const DEFAULT_STOP_TIMEOUT_MS = 5_000;

/**
 * 管理每个应用的独立 backend Worker、可靠启动握手和停止握手。
 *
 * runtime 状态只在 Worker backend/RPC 真正 ready 后进入 running。启动和停止均按 appId
 * 共享操作 Promise，异常会保留 failed 状态供应用中心展示；停止开始后先从可调用集合移除
 * runtime，并拒绝全部待处理 RPC。Worker 有固定 5 秒机会完成业务清理并确认，只有超时才
 * 调用 terminate，避免 SQLite 等应用私有资源在异步关闭途中被宿主强行截断。
 */
export class AppRuntimeManager {
  private readonly runtimes = new Map<string, RuntimeRecord>();
  private readonly states = new Map<string, AppRuntimeState>();
  private readonly startOperations = new Map<string, Promise<void>>();
  private readonly stopOperations = new Map<string, Promise<void>>();
  private readonly events = new EventEmitter();
  private readonly notifications = new EventEmitter();

  public constructor(private readonly options: AppRuntimeManagerOptions = {}) {}

  public getState(appId: string): AppRuntimeState {
    return this.states.get(appId) ?? 'stopped';
  }

  public start(startOptions: AppRuntimeStartOptions): Promise<void> {
    const existing = this.startOperations.get(startOptions.appId);
    if (existing) return existing;
    if (this.getState(startOptions.appId) === 'running') return Promise.resolve();
    const operation = this.startRuntime(startOptions);
    this.startOperations.set(startOptions.appId, operation);
    const clear = () => {
      if (this.startOperations.get(startOptions.appId) === operation) this.startOperations.delete(startOptions.appId);
    };
    void operation.then(clear, clear);
    return operation;
  }

  private async startRuntime(startOptions: AppRuntimeStartOptions): Promise<void> {
    this.states.set(startOptions.appId, 'starting');
    if (startOptions.manifest.runtime.kind === 'web') {
      this.runtimes.set(startOptions.appId, { options: startOptions, pending: new Map() });
      this.states.set(startOptions.appId, 'running');
      return;
    }
    let worker: AppRuntimeWorker;
    try {
      worker = (this.options.createWorker ?? createDefaultWorker)({
        appId: startOptions.appId,
        backendEntry: join(startOptions.installPath, startOptions.manifest.runtime.backendEntry),
        dataDirectory: startOptions.dataDirectory,
        manifest: startOptions.manifest
      });
    } catch (error) {
      const failure = new Error(`无法创建应用 ${startOptions.appId} Worker：${errorMessage(error)}`);
      this.markRuntimeFailed(startOptions.appId, failure);
      throw failure;
    }
    const runtime: RuntimeRecord = { options: startOptions, worker, pending: new Map() };
    this.runtimes.set(startOptions.appId, runtime);

    const timeoutMs = this.options.startTimeoutMs ?? DEFAULT_START_TIMEOUT_MS;
    const logger = this.options.logger ?? console;
    return new Promise<void>((resolve, reject) => {
      let startupSettled = false;
      let timeout: NodeJS.Timeout | undefined;
      const clearTimeoutIfNeeded = () => {
        if (timeout) clearTimeout(timeout);
      };
      const failStartup = (failure: Error, terminate = false) => {
        if (startupSettled) return;
        startupSettled = true;
        clearTimeoutIfNeeded();
        this.failRuntime(startOptions.appId, failure, runtime);
        if (terminate) {
          logger.error(`应用 ${startOptions.appId} 启动超时（${timeoutMs}ms），正在强制终止 Worker。`);
          void worker.terminate().catch((error) => logger.error(`应用 ${startOptions.appId} 启动超时，强制终止 Worker 失败：${errorMessage(error)}`));
        }
        reject(failure);
      };
      runtime.cancelStart = (failure) => failStartup(failure);
      const onMessage = (message: unknown) => {
        if (this.runtimes.get(startOptions.appId) !== runtime) return;
        if (isReadyMessage(message)) {
          if (startupSettled) return;
          startupSettled = true;
          clearTimeoutIfNeeded();
          if (this.runtimes.get(startOptions.appId) !== runtime) {
            const failure = new Error(`应用 ${startOptions.appId} 在启动完成前已停止`);
            this.markRuntimeFailed(startOptions.appId, failure);
            reject(failure);
            return;
          }
          this.states.set(startOptions.appId, 'running');
          resolve();
          return;
        }
        this.handleMessage(startOptions.appId, message);
      };
      const onError = (error: Error) => {
        const failure = new Error(`应用 Worker 异常：${error.message}`);
        if (!startupSettled) failStartup(failure);
        else this.failRuntime(startOptions.appId, failure, runtime);
      };
      const onExit = (code: number) => {
        if (!startupSettled) {
          failStartup(new Error(`应用 ${startOptions.appId} 启动前退出，退出码：${code}`));
        } else {
          this.failRuntime(startOptions.appId, new Error(`应用 Worker 异常退出，退出码：${code}`), runtime);
        }
      };
      worker.on('message', onMessage);
      worker.once('error', onError);
      worker.once('exit', onExit);
      timeout = setTimeout(() => failStartup(new Error(`应用 ${startOptions.appId} 启动超时（${timeoutMs}ms）`), true), timeoutMs);
    });
  }

  public invoke(appId: string, method: string, payload: unknown): Promise<unknown> {
    const runtime = this.runtimes.get(appId);
    const state = this.getState(appId);
    if (state !== 'running') {
      return Promise.reject(new Error(state === 'stopped' || state === 'stopping' ? `应用尚未启动：${appId}` : `应用尚未运行：${appId}`));
    }
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
    this.states.set(appId, 'stopping');
    runtime.cancelStart?.(new Error(`应用 ${appId} 在启动完成前被停止`));
    this.rejectPending(runtime, new Error(`应用正在停止：${appId}`));
    const operation = runtime.worker ? this.stopWorker(appId, runtime.worker) : Promise.resolve();
    const tracked = operation.then(
      () => { this.states.set(appId, 'stopped'); },
      (error) => { this.states.set(appId, 'failed'); throw error; }
    );
    this.stopOperations.set(appId, tracked);
    const clear = () => { if (this.stopOperations.get(appId) === tracked) this.stopOperations.delete(appId); };
    void tracked.then(clear, clear);
    return tracked;
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

  /** 只转发已获 manifest 授权的通知，并以运行时记录绑定应用身份。 */
  public onNotification(listener: (notification: AppRuntimeNotification) => void): () => void {
    this.notifications.on('notification', listener);
    return () => this.notifications.off('notification', listener);
  }

  private handleMessage(appId: string, message: unknown): void {
    const runtime = this.runtimes.get(appId);
    if (!runtime || !message || typeof message !== 'object') return;
    const value = message as Partial<AppWorkerResponse> & Partial<AppWorkerEvent> & Partial<AppWorkerNotification>;
    if (value.type === 'response' && value.requestId) {
      const pending = runtime.pending.get(value.requestId);
      if (!pending) return;
      runtime.pending.delete(value.requestId);
      value.ok ? pending.resolve(value.result) : pending.reject(new Error(value.errorMessage ?? '应用请求失败'));
      return;
    }
    if (value.type === 'notification') {
      if (!runtime.options.manifest.capabilities.includes('notification.show')) {
        (this.options.logger ?? console).error(`应用 ${appId} 未声明 notification.show，已拒绝 backend 通知请求。`);
        return;
      }
      this.notifications.emit('notification', { appId, payload: value.payload } satisfies AppRuntimeNotification);
      return;
    }
    if (value.type === 'event' && value.event) this.events.emit('event', { appId, event: value.event, payload: value.payload } satisfies AppHostEvent);
  }

  private failRuntime(appId: string, error: Error, expectedRuntime?: RuntimeRecord): void {
    const runtime = this.runtimes.get(appId);
    if (!runtime || (expectedRuntime && runtime !== expectedRuntime)) return;
    this.runtimes.delete(appId);
    this.markRuntimeFailed(appId, error);
    this.rejectPending(runtime, error);
  }

  private markRuntimeFailed(appId: string, error: Error): void {
    this.states.set(appId, 'failed');
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

function isReadyMessage(message: unknown): message is AppWorkerReady {
  return Boolean(message && typeof message === 'object' && (message as Partial<AppWorkerReady>).type === 'ready');
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function createDefaultWorker(workerData: Record<string, unknown>): AppRuntimeWorker {
  return new Worker(join(__dirname, 'app-backend-worker.js'), { workerData });
}
