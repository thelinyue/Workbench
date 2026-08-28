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
  onKeyboardCapture?(key: 'Tab', enabled: boolean): void;
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
  if (data.appId !== options.appId) return false;

  if (data.type === 'workbench-app-keyboard-capture') {
    if (data.key !== 'Tab' || typeof data.enabled !== 'boolean' || !options.onKeyboardCapture) return false;
    options.onKeyboardCapture('Tab', data.enabled);
    return true;
  }

  if (!isNonEmptyString(data.requestId)) return false;

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

interface ForwardCapturedHostedAppKeyOptions {
  key: string;
  captureTab: boolean;
  frameWasFocused: boolean;
  preventDefault(): void;
  focusFrame(): void;
  postMessage(message: unknown, targetOrigin: string): void;
  allowedOrigin: string;
}

/**
 * 将 Chromium 从跨来源 iframe 抢回宿主 BODY 的 Tab 送回申请捕获的应用。
 * 捕获状态由应用输入区按焦点临时声明，宿主只处理通用键盘协议，不理解终端会话或 SSH 业务。
 */
export function forwardCapturedHostedAppKey(options: ForwardCapturedHostedAppKeyOptions): boolean {
  if (options.key !== 'Tab' || !options.captureTab || !options.frameWasFocused) return false;
  options.preventDefault();
  options.focusFrame();
  options.postMessage({ type: 'workbench-app-keyboard-input', key: 'Tab' }, options.allowedOrigin);
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
  const captureTabRef = useRef(false);
  const frameFocusedRef = useRef(false);
  // SSH 终端的连接表单和主机密钥确认需要表单、模态框能力，其他应用继续使用更严格的沙箱。
  const sandbox = appId === 'terminal' ? 'allow-scripts allow-same-origin allow-forms allow-modals' : 'allow-scripts allow-same-origin';

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
    const frame = frameRef.current;
    if (!frame) return;
    const postMessage = (message: unknown, targetOrigin: string) => frameWindow.postMessage(message, targetOrigin);
    const onMessage = (event: MessageEvent) => {
      void routeHostedAppMessage({
        appId, frameWindow, allowedOrigin, event, bridge: window.workbench.apps, postMessage,
        onKeyboardCapture: (_key, enabled) => {
          captureTabRef.current = enabled;
          if (enabled) frameFocusedRef.current = true;
        }
      });
    };
    const onFrameFocus = () => {
      frameFocusedRef.current = true;
    };
    const clearKeyboardCapture = () => {
      captureTabRef.current = false;
      frameFocusedRef.current = false;
    };
    const onPointerDown = (event: PointerEvent) => {
      if (event.target !== frame) clearKeyboardCapture();
    };
    const onKeyDown = (event: KeyboardEvent) => {
      forwardCapturedHostedAppKey({
        key: event.key,
        captureTab: captureTabRef.current,
        frameWasFocused: frameFocusedRef.current,
        preventDefault: () => event.preventDefault(),
        focusFrame: () => frame.focus(),
        postMessage,
        allowedOrigin
      });
    };
    window.addEventListener('message', onMessage);
    window.addEventListener('keydown', onKeyDown, true);
    window.addEventListener('pointerdown', onPointerDown, true);
    window.addEventListener('blur', clearKeyboardCapture);
    frame.addEventListener('focus', onFrameFocus);
    const unsubscribe = window.workbench.apps.onEvent((event) => {
      forwardHostedAppEvent({ appId, event, allowedOrigin, postMessage });
    });
    return () => {
      window.removeEventListener('message', onMessage);
      window.removeEventListener('keydown', onKeyDown, true);
      window.removeEventListener('pointerdown', onPointerDown, true);
      window.removeEventListener('blur', clearKeyboardCapture);
      frame.removeEventListener('focus', onFrameFocus);
      clearKeyboardCapture();
      unsubscribe();
    };
  }, [appId, entryUrl]);

  return <iframe
    ref={frameRef}
    className="embedded-app-frame hosted-app-frame"
    title={name}
    src={entryUrl}
    sandbox={sandbox}
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
