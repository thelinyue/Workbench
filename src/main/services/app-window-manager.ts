import type { Event as ElectronEvent, WillResizeDetails } from 'electron';
import type { AppHostEvent, AppWindowManifest } from '../../shared/app-contract';
import type { AppWindowState } from '../data/app-window-state-repository';
import { installHostNavigationGuard, resolveWorkbenchRendererUrl, type HostNavigationWebContents } from './host-navigation-guard';

export interface AppWindowStateStore {
  load(appId: string, windowKey: string): AppWindowState | undefined;
  upsert(state: AppWindowState): void;
}

export interface AppWindowHost {
  readonly webContents: HostNavigationWebContents & { id: number; send(channel: string, value: unknown): void };
  isMinimized(): boolean;
  restore(): void;
  show(): void;
  focus(): void;
  getNormalBounds(): { x: number; y: number; width: number; height: number };
  isMaximized(): boolean;
  maximize(): void;
  close(): void;
  destroy(): void;
  loadURL(url: string): Promise<void>;
  loadFile(path: string, options?: { query?: Record<string, string> }): Promise<void>;
  on(event: 'close', listener: () => void): this;
  on(event: 'will-move', listener: (event: ElectronEvent, newBounds: Rectangle) => void): this;
  on(event: 'will-resize', listener: (event: ElectronEvent, newBounds: Rectangle, details: WillResizeDetails) => void): this;
  removeListener(event: 'close', listener: () => void): this;
  removeListener(event: 'will-move', listener: (event: ElectronEvent, newBounds: Rectangle) => void): this;
  removeListener(event: 'will-resize', listener: (event: ElectronEvent, newBounds: Rectangle, details: WillResizeDetails) => void): this;
  once(event: 'close' | 'closed' | 'ready-to-show', listener: () => void): this;
}

export interface AppWindowOpenOptions {
  appId: string;
  windowKey?: string;
  name: string;
  window: AppWindowManifest;
}

export interface AppWindowCreationOptions {
  x: number;
  y: number;
  width: number;
  height: number;
  minWidth: number;
  minHeight: number;
  title: string;
  frame: false;
  modal: false;
  skipTaskbar: false;
  show: false;
  webPreferences: {
    preload: string;
    contextIsolation: true;
    nodeIntegration: false;
    sandbox: true;
  };
}

export interface AppWindowManagerOptions {
  stateStore: AppWindowStateStore;
  createWindow(options: AppWindowCreationOptions): AppWindowHost;
  getDisplays(): Array<{ workArea: { x: number; y: number; width: number; height: number } }>;
  getPrimaryDisplay(): { workArea: { x: number; y: number; width: number; height: number } };
  preloadPath: string;
  rendererUrl?: string;
  rendererFile?: string;
  logger?: Pick<Console, 'error'>;
}

interface Rectangle {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface AppWindowIdentity {
  appId: string;
  windowKey: string;
}

/**
 * 管理应用原生窗口的唯一身份、展示恢复和 webContents 反向解析。
 *
 * 管理器只认通用的 appId/windowKey，不包含分析中心等具体应用逻辑；关闭窗口仅清理
 * 展示层映射并保存状态，不触碰应用 runtime，因此业务生命周期仍由运行时管理器独立负责。
 * 同键窗口加载期间共享同一个 Promise；加载失败必须清理映射并销毁隐藏窗口，调用方只有在
 * renderer 真正加载完成后才会收到成功结果，后续启动也才能安全重试。
 */
export class AppWindowManager {
  private readonly windows = new Map<string, AppWindowHost>();
  private readonly webContentsIds = new Map<string, number>();
  private readonly identities = new Map<number, Readonly<AppWindowIdentity>>();
  private readonly openings = new Map<string, Promise<AppWindowHost>>();
  private readonly exitManagedWindows = new WeakSet<AppWindowHost>();
  private readonly lastUserBounds = new WeakMap<AppWindowHost, Rectangle>();
  private readonly eventReadyKeys = new Set<string>();
  private readonly pendingEvents = new Map<string, AppHostEvent[]>();
  private closing = false;
  private closeOperation: Promise<void> | undefined;

  public constructor(private readonly options: AppWindowManagerOptions) {}

  public open(options: AppWindowOpenOptions): Promise<AppWindowHost> {
    if (this.closing) return Promise.reject(new Error('应用窗口管理器正在关闭，不能创建新窗口'));
    const identity = Object.freeze({ appId: options.appId, windowKey: options.windowKey ?? 'main' });
    const mapKey = identityKey(identity);
    const opening = this.openings.get(mapKey);
    if (opening) return opening;

    const existing = this.windows.get(mapKey);
    if (existing) {
      if (existing.isMinimized()) existing.restore();
      existing.show();
      existing.focus();
      // 桌面、开始菜单和系统通知都可能复用已有窗口；通知应用窗口已重新激活，应用应重新读取持久化状态。
      this.deliverEvent(identity.appId, identity.windowKey, { appId: identity.appId, event: 'host.window.activated', payload: undefined });
      return Promise.resolve(existing);
    }

    const pending = this.createAndLoadWindow(options, identity, mapKey);
    this.openings.set(mapKey, pending);
    const clearOpening = () => { if (this.openings.get(mapKey) === pending) this.openings.delete(mapKey); };
    void pending.then(clearOpening, clearOpening);
    return pending;
  }

  private async createAndLoadWindow(options: AppWindowOpenOptions, identity: Readonly<AppWindowIdentity>, mapKey: string): Promise<AppWindowHost> {
    const saved = this.options.stateStore.load(identity.appId, identity.windowKey);
    const bounds = restoreBounds(saved, options.window, this.options.getDisplays(), this.options.getPrimaryDisplay().workArea);
    const window = this.options.createWindow({
      ...bounds,
      minWidth: options.window.minSize.width,
      minHeight: options.window.minSize.height,
      title: options.name,
      frame: false,
      modal: false,
      skipTaskbar: false,
      show: false,
      webPreferences: {
        preload: this.options.preloadPath,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true
      }
    });
    // Electron 在 BrowserWindow 销毁后会让 webContents getter 抛错，身份编号必须在创建时固定下来。
    const webContentsId = window.webContents.id;
    this.windows.set(mapKey, window);
    this.webContentsIds.set(mapKey, webContentsId);
    this.identities.set(webContentsId, identity);
    this.lastUserBounds.set(window, { ...bounds });
    let persistState = true;

    window.once('ready-to-show', () => window.show());
    // will-resize/will-move 只由用户直接拖动原生窗口触发，系统布局调整不会污染用户边界。
    const rememberMovedBounds = (_event: unknown, nextBounds: Rectangle) => {
      const previous = this.lastUserBounds.get(window) ?? window.getNormalBounds();
      this.lastUserBounds.set(window, { ...previous, x: nextBounds.x, y: nextBounds.y });
    };
    const rememberResizedBounds = (_event: unknown, nextBounds: Rectangle) => {
      this.lastUserBounds.set(window, { ...nextBounds });
    };
    const persistWindowState = () => {
      if (!persistState || this.exitManagedWindows.has(window)) return;
      try {
        const normal = this.lastUserBounds.get(window) ?? window.getNormalBounds();
        this.options.stateStore.upsert({ ...identity, ...normal, maximized: window.isMaximized() });
      } catch (error) {
        (this.options.logger ?? console).error(`保存应用窗口状态失败（${identity.appId}/${identity.windowKey}）：${errorMessage(error)}`);
      }
    };
    window.on('close', persistWindowState);
    window.on('will-move', rememberMovedBounds);
    window.on('will-resize', rememberResizedBounds);
    window.once('closed', () => {
      window.removeListener('close', persistWindowState);
      window.removeListener('will-move', rememberMovedBounds);
      window.removeListener('will-resize', rememberResizedBounds);
      this.removeWindowMappings(mapKey, window, webContentsId);
    });

    if (saved?.maximized) window.maximize();
    try {
      const trustedRendererUrl = resolveWorkbenchRendererUrl({
        rendererUrl: this.options.rendererUrl,
        rendererFile: this.options.rendererFile,
        surface: 'app-window'
      });
      installHostNavigationGuard(window.webContents, trustedRendererUrl);
      await this.loadRenderer(window, trustedRendererUrl);
      return window;
    } catch (error) {
      persistState = false;
      this.removeWindowMappings(mapKey, window, webContentsId);
      window.destroy();
      throw new Error(`应用窗口加载 Workbench renderer 失败：${error instanceof Error ? error.message : String(error)}`, { cause: error });
    }
  }

  /** 返回不可变窗口身份，供 IPC 根据发送方 webContents 做通用上下文解析。 */
  public resolveWebContents(webContentsId: number): Readonly<AppWindowIdentity> | undefined {
    return this.identities.get(webContentsId);
  }

  /**
   * 冷启动时 Workbench renderer 可能尚未建立 iframe 事件订阅，因此按 appId/windowKey 排队。
   * ready 信号同样由 sender-bound webContents 解析，renderer 不能替其他应用领取事件。
   */
  public deliverEvent(appId: string, windowKey: string, event: AppHostEvent): void {
    if (event.appId !== appId) throw new Error('应用窗口事件身份与目标应用不一致');
    const mapKey = identityKey({ appId, windowKey });
    const window = this.windows.get(mapKey);
    if (window && this.eventReadyKeys.has(mapKey)) {
      window.webContents.send('workbench:app-event', event);
      return;
    }
    const pending = this.pendingEvents.get(mapKey) ?? [];
    pending.push(event);
    this.pendingEvents.set(mapKey, pending);
  }

  public markEventSurfaceReady(webContentsId: number): void {
    const identity = this.identities.get(webContentsId);
    if (!identity) throw new Error('找不到应用窗口身份，无法确认事件表面已就绪');
    const mapKey = identityKey(identity);
    const window = this.windows.get(mapKey);
    if (!window || window.webContents.id !== webContentsId) throw new Error('应用窗口已关闭，无法确认事件表面已就绪');
    this.eventReadyKeys.add(mapKey);
    const pending = this.pendingEvents.get(mapKey) ?? [];
    this.pendingEvents.delete(mapKey);
    for (const event of pending) window.webContents.send('workbench:app-event', event);
  }

  /**
   * 最终退出时在状态仓储仍开放期间显式保存窗口状态，再用 destroy 强制销毁 Presentation Host。
   * 退出不能依赖可被 close/beforeunload 取消的常规关闭事件；窗口一旦进入此路径，其普通 close
   * listener 将永久停止持久化，即使保存或 destroy 失败，仓储关闭后也不会由幸存窗口再次写入。
   * 单窗失败不阻断其他窗口收口，最终聚合为中文错误；并发调用共享同一个 Promise。
   */
  public closeAll(): Promise<void> {
    if (this.closeOperation) return this.closeOperation;
    this.closing = true;
    this.closeOperation = Promise.resolve().then(() => {
      return this.closeWindows(() => true, false);
    });
    return this.closeOperation;
  }

  /**
   * 收口单个应用的所有窗口。窗口创建和 renderer 加载是异步的，必须先等待该应用当前
   * 已登记的 opening 结算，再读取窗口映射；否则卸载/停用可能遗漏刚刚创建的窗口。
   */
  public async closeApp(appId: string): Promise<void> {
    const openings = [...this.openings.entries()]
      .filter(([mapKey]) => mapKeyAppId(mapKey) === appId)
      .map(([, opening]) => opening);
    await Promise.allSettled(openings);
    await this.closeWindows((mapKey) => mapKeyAppId(mapKey) === appId, true);
  }

  private closeWindows(shouldClose: (mapKey: string) => boolean, preserveMappingsOnDestroyFailure: boolean): void {
    const targets = [...this.windows.entries()].filter(([mapKey]) => shouldClose(mapKey));
    const failures: string[] = [];

    // 必须先抑制全部普通 close listener，避免销毁某个窗口时连带触发其他窗口重复保存。
    for (const [, window] of targets) this.exitManagedWindows.add(window);

    for (const [mapKey, window] of targets) {
      const webContentsId = this.webContentsIds.get(mapKey);
      const identity = webContentsId === undefined ? undefined : this.identities.get(webContentsId);
      const label = identity ? `${identity.appId}/${identity.windowKey}` : `webContents#${String(webContentsId)}`;
      try {
        if (!identity) throw new Error('缺少应用窗口身份');
        const normal = this.lastUserBounds.get(window) ?? window.getNormalBounds();
        this.options.stateStore.upsert({ ...identity, ...normal, maximized: window.isMaximized() });
      } catch (error) {
        failures.push(`保存应用窗口状态失败（${label}）：${errorMessage(error)}`);
      }

      let destroyed = false;
      try {
        window.destroy();
        destroyed = true;
      } catch (error) {
        failures.push(`强制销毁应用窗口失败（${label}）：${errorMessage(error)}`);
      } finally {
        // 单应用收口失败时保留身份和窗口映射，调用方才能修复后再次 closeApp；最终退出
        // 则必须清理映射，避免仓储关闭后幸存窗口继续触发普通 close listener。
        if (destroyed || !preserveMappingsOnDestroyFailure) this.removeWindowMappings(mapKey, window, webContentsId);
      }
    }

    if (failures.length > 0) throw new Error(`关闭应用窗口失败：${failures.join('；')}`);
  }

  private loadRenderer(window: AppWindowHost, trustedRendererUrl: string): Promise<void> {
    if (this.options.rendererUrl) {
      // 应用身份由 webContents 映射提供，URL 只声明 renderer 表面，避免身份泄漏或伪造。
      return window.loadURL(trustedRendererUrl);
    }
    if (this.options.rendererFile) return window.loadFile(this.options.rendererFile, { query: { surface: 'app-window' } });
    return Promise.reject(new Error('缺少 Workbench renderer 加载地址。'));
  }

  private removeWindowMappings(mapKey: string, window: AppWindowHost, webContentsId: number | undefined): void {
    if (this.windows.get(mapKey) === window) {
      this.windows.delete(mapKey);
      this.webContentsIds.delete(mapKey);
      this.eventReadyKeys.delete(mapKey);
      this.pendingEvents.delete(mapKey);
    }
    if (webContentsId !== undefined) this.identities.delete(webContentsId);
    this.lastUserBounds.delete(window);
  }
}

function identityKey(identity: AppWindowIdentity): string {
  return JSON.stringify([identity.appId, identity.windowKey]);
}

function mapKeyAppId(mapKey: string): string | undefined {
  try {
    const identity = JSON.parse(mapKey) as unknown;
    return Array.isArray(identity) && typeof identity[0] === 'string' ? identity[0] : undefined;
  } catch {
    return undefined;
  }
}

/**
 * 将已保存的 Electron DIP 边界恢复到当前显示器拓扑。
 * 完全离屏通常意味着显示器已拔除，此时旧位置和旧尺寸都不可沿用，必须将 manifest 默认尺寸
 * 限制在主屏工作区后居中；仍有交集时则使用对应工作区裁剪，确保窗口不会只露出一角而无法操作。
 */
function restoreBounds(
  saved: AppWindowState | undefined,
  manifest: AppWindowManifest,
  displays: Array<{ workArea: Rectangle }>,
  primaryWorkArea: Rectangle
): Rectangle {
  const matchedWorkArea = saved ? displays.map((display) => display.workArea).find((workArea) => intersects(saved, workArea)) : undefined;
  if (!saved || !matchedWorkArea) return centeredBounds(manifest.defaultSize, manifest.minSize, primaryWorkArea);

  const width = Math.max(manifest.minSize.width, Math.min(saved.width, matchedWorkArea.width));
  const height = Math.max(manifest.minSize.height, Math.min(saved.height, matchedWorkArea.height));
  return {
    x: clamp(saved.x, matchedWorkArea.x, matchedWorkArea.x + matchedWorkArea.width - width),
    y: clamp(saved.y, matchedWorkArea.y, matchedWorkArea.y + matchedWorkArea.height - height),
    width,
    height
  };
}

function centeredBounds(
  size: { width: number; height: number },
  minimumSize: { width: number; height: number },
  workArea: Rectangle
): Rectangle {
  const width = Math.max(minimumSize.width, Math.min(size.width, workArea.width));
  const height = Math.max(minimumSize.height, Math.min(size.height, workArea.height));
  return {
    x: workArea.x + Math.floor((workArea.width - width) / 2),
    y: workArea.y + Math.floor((workArea.height - height) / 2),
    width,
    height
  };
}

function intersects(first: Rectangle, second: Rectangle): boolean {
  return first.x < second.x + second.width && first.x + first.width > second.x
    && first.y < second.y + second.height && first.y + first.height > second.y;
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(Math.max(value, minimum), maximum);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
