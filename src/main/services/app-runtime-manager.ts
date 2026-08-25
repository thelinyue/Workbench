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

export interface AppRuntimeWorker {
  postMessage(message: unknown): void;
  terminate(): Promise<number>;
  on(event: 'message', listener: (message: unknown) => void): this;
  once(event: 'error', listener: (error: Error) => void): this;
  once(event: 'exit', listener: (code: number) => void): this;
}

interface RuntimeRecord {
  options: AppRuntimeStartOptions;
  worker?: AppRuntimeWorker;
  pending: Map<string, { resolve: (value: unknown) => void; reject: (error: Error) => void }>;
}

export interface AppRuntimeManagerOptions {
  createWorker?: (workerData: Record<string, unknown>) => AppRuntimeWorker;
}

/**
 * 管理每个内嵌应用的独立 backend Worker。
 * Worker 崩溃只影响当前应用的 RPC，不会把应用业务状态混入工作台主进程。
 */
export class AppRuntimeManager {
  private readonly runtimes = new Map<string, RuntimeRecord>();
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

  public async stop(appId: string): Promise<void> {
    const runtime = this.runtimes.get(appId);
    if (!runtime) return;
    this.runtimes.delete(appId);
    this.rejectPending(runtime, new Error(`应用已停止：${appId}`));
    if (runtime.worker) await runtime.worker.terminate();
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
}

function createDefaultWorker(workerData: Record<string, unknown>): AppRuntimeWorker {
  return new Worker(join(__dirname, 'app-backend-worker.js'), { workerData });
}
