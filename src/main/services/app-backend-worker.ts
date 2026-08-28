import { mkdir } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import { parentPort, workerData } from 'node:worker_threads';
import { createBackendWorkerSession, type AppBackend } from './app-backend-worker-session';
import type { AppBackendContext } from '../../shared/app-contract';

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
  const backend = await module.createAppBackend({
    appId: input.appId,
    dataDirectory: input.dataDirectory,
    manifest: input.manifest,
    emit: (event, payload) => parentPort?.postMessage({ type: 'event', appId: input.appId, event, payload }),
    showNotification: (payload) => parentPort?.postMessage({ type: 'notification', payload })
  });
  createBackendWorkerSession(parentPort, backend);
}
