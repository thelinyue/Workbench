/**
 * 工作台与内嵌应用之间的稳定契约。
 *
 * 应用包只能依赖这些公开类型和 Host API，不得引用工作台主进程内部服务，
 * 这样应用才能在不修改工作台版本的情况下独立发布。
 */
export interface AppManifestV1 {
  schemaVersion: 1;
  id: string;
  name: string;
  description: string;
  publisherId: string;
  version: string;
  hostApiVersion: string;
  minWorkbenchVersion: string;
  window?: AppWindowManifest;
  runtime: AppRuntimeV1;
  capabilities: string[];
}

/**
 * 原生独立应用窗口的初始与最小尺寸。
 *
 * 该字段只描述窗口宿主的通用尺寸约束，不承载应用特有的窗口 DSL，
 * 以保持应用包可独立发布且由宿主统一管理窗口生命周期。
 */
export interface AppWindowManifest {
  defaultSize: AppWindowSize;
  minSize: AppWindowSize;
}

/** 原生窗口的像素尺寸。 */
export interface AppWindowSize {
  width: number;
  height: number;
}

/** 应用 renderer 的运行方式；backendEntry 保持可选以支持纯静态 Web 工具。 */
export type AppRuntimeV1 =
  | { kind: 'web'; rendererEntry: string; icon: string }
  | { kind?: 'backend'; rendererEntry: string; backendEntry: string; icon: string };

export interface AppCatalogRelease {
  version: string;
  hostApiVersion: string;
  minWorkbenchVersion: string;
  url: string;
  size: number;
  sha256: string;
  signature: { keyId: string; signature: string };
}

export interface AppCatalogItem {
  id: string;
  name: string;
  description: string;
  publisherId: string;
  releases: AppCatalogRelease[];
}

export interface AppCatalogDocumentV1 {
  schemaVersion: 1;
  apps: AppCatalogItem[];
}

export type AppInstallState = 'not-installed' | 'installed' | 'update-available' | 'incompatible' | 'broken' | 'installing';
export type AppRuntimeState = 'stopped' | 'starting' | 'running' | 'stopping' | 'failed';

export interface AppInstallRecord {
  id: string;
  name: string;
  description: string;
  publisherId: string;
  installedVersion?: string;
  availableVersion?: string;
  activeVersion?: string;
  installPath?: string;
  /** 应用是否允许被生命周期协调器自动启动和调用。 */
  enabled: boolean;
  /** 仅由未打包开发版运行时附加，不写入本地应用注册表。 */
  developmentOverride?: boolean;
  state: AppInstallState;
  errorMessage?: string;
}

export interface AppCatalogSnapshot {
  catalog: AppCatalogDocumentV1;
  fetchedAt: string;
  fromCache: boolean;
  warning?: string;
}

export interface AppRpcRequest {
  appId: string;
  requestId: string;
  method: string;
  payload: unknown;
}

export interface AppRpcResponse {
  requestId: string;
  ok: boolean;
  result?: unknown;
  errorMessage?: string;
}

export interface AppHostEvent {
  appId: string;
  event: string;
  payload: unknown;
}

/** backend 请求宿主展示的系统通知；应用身份始终由 Worker 运行时补充。 */
export interface AppNotificationRequest {
  title: string;
  body: string;
  windowKey?: string;
  activationPayload?: unknown;
}

/**
 * 应用 backend 的稳定启动上下文。
 *
 * 通知接口不接收 appId，避免应用包伪造其他应用身份；宿主根据实际 Worker 记录执行能力校验。
 */
export interface AppBackendContext {
  appId: string;
  dataDirectory: string;
  manifest: unknown;
  emit(event: string, payload: unknown): void;
  showNotification(notification: AppNotificationRequest): void;
}

/** 启动后 renderer 应采用的宿主展示形态。 */
export interface AppLaunchResult {
  presentation: 'app-window' | 'embedded';
}

/**
 * 原生应用窗口 renderer 启动所需的完整宿主上下文。
 *
 * appId/windowKey 来自主进程维护的 webContents 身份映射，renderer 不得自行传入；
 * 资源地址则由当前有效 manifest 与开发覆盖状态派生，避免页面伪造其他应用身份或版本。
 */
export interface AppWindowContext {
  appId: string;
  windowKey: string;
  name: string;
  entryUrl: string;
  iconUrl: string;
  developmentOverride: boolean;
}
