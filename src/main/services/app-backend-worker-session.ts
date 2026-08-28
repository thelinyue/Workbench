export interface AppBackend {
  invoke(method: string, payload: unknown): Promise<unknown> | unknown;
  close?(): Promise<void> | void;
}

export interface BackendWorkerPort {
  postMessage(message: Record<string, unknown>): void;
  on(event: 'message', listener: (message: unknown) => void): this;
  close(): void;
}

/**
 * 隔离 backend Worker 的消息接收与停止顺序，便于使用 fake port 验证真实握手行为。
 *
 * stop 一到达便关闭接单开关；已经开始的 invoke 仍保留原响应并全部结算，随后才执行可选的
 * backend.close。无论在途请求或 close 是否失败，都只发送一次 stopped 确认，最后关闭通信端口，
 * 让 worker_threads 自然退出，不依赖 process.exit 截断异步清理。
 */
export function createBackendWorkerSession(port: BackendWorkerPort, backend: AppBackend): void {
  const inFlight = new Set<Promise<void>>();
  let acceptingInvokes = true;
  let stopping: Promise<void> | undefined;

  const invoke = (requestId: string, method: string, payload: unknown) => {
    if (!acceptingInvokes) {
      port.postMessage({ type: 'response', requestId, ok: false, errorMessage: '应用 backend 正在关闭，不能接受新请求' });
      return;
    }
    const operation = Promise.resolve()
      .then(() => backend.invoke(method, payload))
      .then(
        (result) => { port.postMessage({ type: 'response', requestId, ok: true, result }); },
        (error) => {
          const message = errorMessage(error);
          port.postMessage({ type: 'response', requestId, ok: false, errorMessage: message });
          throw new Error(message);
        }
      );
    inFlight.add(operation);
    void operation.then(() => inFlight.delete(operation), () => inFlight.delete(operation));
  };

  const stop = () => {
    if (stopping) return;
    acceptingInvokes = false;
    stopping = (async () => {
      const failures: string[] = [];
      const invokeResults = await Promise.allSettled([...inFlight]);
      for (const result of invokeResults) {
        if (result.status === 'rejected') failures.push(`在途请求失败：${errorMessage(result.reason)}`);
      }
      try {
        await backend.close?.();
      } catch (error) {
        failures.push(errorMessage(error));
      }
      const acknowledgement = failures.length === 0
        ? { type: 'stopped', ok: true }
        : { type: 'stopped', ok: false, errorMessage: `应用 backend 关闭失败：${failures.join('；')}` };
      try {
        port.postMessage(acknowledgement);
      } finally {
        port.close();
      }
    })();
  };

  port.on('message', (message: unknown) => {
    if (!message || typeof message !== 'object') return;
    const value = message as { type?: string; requestId?: string; method?: string; payload?: unknown };
    if (value.type === 'stop') { stop(); return; }
    if (value.type === 'invoke' && value.requestId && value.method) invoke(value.requestId, value.method, value.payload);
  });
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
