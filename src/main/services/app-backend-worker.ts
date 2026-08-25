import { mkdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { parentPort, workerData } from 'node:worker_threads';

interface AppBackendContext {
  appId: string;
  dataDirectory: string;
  manifest: unknown;
  emit(event: string, payload: unknown): void;
}

interface AppBackend {
  invoke(method: string, payload: unknown): Promise<unknown> | unknown;
  close?(): Promise<void> | void;
}

interface AppBackendModule {
  createAppBackend(context: AppBackendContext): Promise<AppBackend> | AppBackend;
}

void startBackend();

async function startBackend(): Promise<void> {
  if (!parentPort) throw new Error('应用 Worker 缺少通信端口');
  const input = workerData as { appId: string; backendEntry: string; dataDirectory: string; manifest: unknown };
  await mkdir(input.dataDirectory, { recursive: true });
  const module = await import(pathToFileURL(input.backendEntry).href) as Partial<AppBackendModule>;
  if (typeof module.createAppBackend !== 'function') throw new Error('应用 backend 缺少 createAppBackend 导出');
  const backend = await module.createAppBackend({ appId: input.appId, dataDirectory: input.dataDirectory, manifest: input.manifest, emit: (event, payload) => parentPort?.postMessage({ type: 'event', appId: input.appId, event, payload }) });
  parentPort.on('message', async (message: unknown) => {
    if (!message || typeof message !== 'object') return;
    const value = message as { type?: string; requestId?: string; method?: string; payload?: unknown };
    if (value.type === 'stop') {
      await backend.close?.();
      process.exit(0);
    }
    if (value.type !== 'invoke' || !value.requestId || !value.method) return;
    try {
      const result = await backend.invoke(value.method, value.payload);
      parentPort?.postMessage({ type: 'response', requestId: value.requestId, ok: true, result });
    } catch (error) {
      parentPort?.postMessage({ type: 'response', requestId: value.requestId, ok: false, errorMessage: error instanceof Error ? error.message : String(error) });
    }
  });
}
