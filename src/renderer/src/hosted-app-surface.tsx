import { useEffect, useRef } from 'react';
import type { AppHostEvent } from '../../shared/app-contract';
import type { WorkbenchBridge } from '../../shared/bridge';
import { toChineseError } from './ui-model';

type HostedAppMessageBridge = Pick<WorkbenchBridge['apps'], 'invoke' | 'getDroppedFilePaths'>;

interface HostedAppMessageEvent {
  source: MessageEventSource | null;
  origin: string;
  data: unknown;
}

interface RouteHostedAppMessageOptions {
  appId: string;
  frameWindow: Window;
  allowedOrigin: string;
  event: HostedAppMessageEvent;
  bridge: HostedAppMessageBridge;
  postMessage(message: unknown, targetOrigin: string): void;
}

interface ForwardHostedAppEventOptions {
  appId: string;
  event: AppHostEvent;
  allowedOrigin: string;
  postMessage(message: unknown, targetOrigin: string): void;
}

/**
 * 校验并转发单个 iframe 消息。
 *
 * source、origin、appId、requestId 和方法名都在边界处验证。文件拖放是纯路径解析协议，
 * 只调用 preload 的 webUtils 路径能力并返回 requestId 对应的本地路径，绝不触发应用 RPC。
 */
export async function routeHostedAppMessage(options: RouteHostedAppMessageOptions): Promise<boolean> {
  if (options.event.source !== options.frameWindow || options.event.origin !== options.allowedOrigin || !isRecord(options.event.data)) return false;
  const data = options.event.data;
  if (data.appId !== options.appId || !isNonEmptyString(data.requestId)) return false;

  if (data.type === 'workbench-app-file-drop') {
    if (!Array.isArray(data.files)) return false;
    try {
      const paths = options.bridge.getDroppedFilePaths(data.files as File[])
        .filter((path): path is string => typeof path === 'string' && Boolean(path.trim()))
        .map((path) => path.trim());
      if (data.files.length > 0 && paths.length === 0) {
        options.postMessage({
          type: 'workbench-app-file-drop-response',
          appId: options.appId,
          requestId: data.requestId,
          ok: false,
          errorMessage: '无法读取拖入文件的本地路径，请使用系统文件管理器重新拖入本机文件。'
        }, options.allowedOrigin);
        return true;
      }
      options.postMessage({
        type: 'workbench-app-file-drop-response',
        appId: options.appId,
        requestId: data.requestId,
        ok: true,
        paths
      }, options.allowedOrigin);
    } catch (error) {
      options.postMessage({
        type: 'workbench-app-file-drop-response',
        appId: options.appId,
        requestId: data.requestId,
        ok: false,
        errorMessage: `无法解析拖放文件路径：${toChineseError(error)}`
      }, options.allowedOrigin);
    }
    return true;
  }

  if (data.type !== 'workbench-app-rpc' || !isNonEmptyString(data.method)) return false;
  try {
    const result = await options.bridge.invoke(options.appId, data.method.trim(), data.payload);
    options.postMessage({
      type: 'workbench-app-rpc-response',
      appId: options.appId,
      requestId: data.requestId,
      ok: true,
      result
    }, options.allowedOrigin);
  } catch (error) {
    options.postMessage({
      type: 'workbench-app-rpc-response',
      appId: options.appId,
      requestId: data.requestId,
      ok: false,
      errorMessage: toChineseError(error)
    }, options.allowedOrigin);
  }
  return true;
}

/** 仅向所属应用的 iframe 转发运行时事件，避免同一页面内不同应用串收消息。 */
export function forwardHostedAppEvent(options: ForwardHostedAppEventOptions): boolean {
  if (options.event.appId !== options.appId) return false;
  options.postMessage({ type: 'workbench-app-event', event: options.event }, options.allowedOrigin);
  return true;
}

/**
 * 从受控应用入口解析唯一允许的消息 origin。
 * Node 的 WHATWG URL 会把自定义协议视为 opaque origin，因此 workbench-app 需要按已注册的
 * 标准 scheme 显式组合 protocol 与 host；其他 URL 只接受浏览器可表示的非 opaque origin。
 */
export function resolveHostedAppOrigin(entryUrl: string): string {
  let url: URL;
  try {
    url = new URL(entryUrl);
  } catch {
    throw new Error(`应用入口地址无效：${entryUrl}`);
  }
  if (url.protocol === 'workbench-app:' && url.host) return `${url.protocol}//${url.host}`;
  if (url.origin !== 'null') return url.origin;
  throw new Error(`应用入口地址缺少可用的安全来源：${entryUrl}`);
}

interface HostedAppSurfaceProps {
  appId: string;
  name: string;
  entryUrl: string;
  onError: (error: unknown) => void;
  onReady?: () => void;
}

/**
 * 通用应用 iframe 表面，只实现 App Host 公共协议，不包含任何应用业务方法。
 * 每次 entryUrl 或组件 key 改变时由 React 重建 iframe，订阅也随该实例完整清理。
 */
export function HostedAppSurface({ appId, name, entryUrl, onError, onReady = () => undefined }: HostedAppSurfaceProps) {
  const frameRef = useRef<HTMLIFrameElement>(null);

  useEffect(() => {
    const frameWindow = frameRef.current?.contentWindow;
    if (!frameWindow || typeof window === 'undefined' || !window.workbench) return;
    let allowedOrigin: string;
    try {
      allowedOrigin = resolveHostedAppOrigin(entryUrl);
    } catch (error) {
      onError(error);
      return;
    }
    const postMessage = (message: unknown, targetOrigin: string) => frameWindow.postMessage(message, targetOrigin);
    const onMessage = (event: MessageEvent) => {
      void routeHostedAppMessage({ appId, frameWindow, allowedOrigin, event, bridge: window.workbench.apps, postMessage });
    };
    window.addEventListener('message', onMessage);
    const unsubscribe = window.workbench.apps.onEvent((event) => {
      forwardHostedAppEvent({ appId, event, allowedOrigin, postMessage });
    });
    return () => {
      window.removeEventListener('message', onMessage);
      unsubscribe();
    };
  }, [appId, entryUrl]);

  return <iframe
    ref={frameRef}
    className="embedded-app-frame hosted-app-frame"
    title={name}
    src={entryUrl}
    sandbox="allow-scripts allow-same-origin"
    onLoad={onReady}
    onError={() => onError(`应用界面加载失败：${name}`)}
  />;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object';
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && Boolean(value.trim());
}
