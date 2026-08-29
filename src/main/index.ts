import { app, BrowserWindow, Menu, screen } from 'electron';
import { join } from 'node:path';
import { registerWorkbenchIpc } from './ipc';
import { AppWindowStateRepository } from './data/app-window-state-repository';
import { loadDevelopmentAppOverride } from './services/app-development-override';
import { AppWindowManager } from './services/app-window-manager';
import { installHostNavigationGuard, resolveWorkbenchRendererUrl } from './services/host-navigation-guard';
import { registerAppProtocolScheme, registerAppResourceProtocol } from './services/app-resource-protocol';
import { WorkbenchLifecycleController, acquireSingleInstance, createOrderedCleanup } from './services/workbench-lifecycle';
import { createMainWindowOptions } from './main-window-options';

// 自定义协议必须在 app.whenReady() 之前声明，否则 iframe 中的 CSS 和 ES module 资源无法按标准方式加载。
registerAppProtocolScheme();

let closeWorkbench: (() => Promise<void>) | undefined;
let unregisterAppProtocol: (() => void) | undefined;
let closeAppWindows: (() => Promise<void>) | undefined;
let closeAppWindowState: (() => void) | undefined;

/**
 * 创建 Workbench 桌面主窗口。
 *
 * 桌面窗口只承载应用中心和旧版内嵌应用；声明 window 的应用由通用 AppWindowManager 创建
 * 独立 BrowserWindow，两类窗口共享同一安全 preload，但不共享父子或模态关系。
 */
function createWindow(): BrowserWindow {
  const rendererFile = join(__dirname, '../renderer/index.html');
  const trustedRendererUrl = resolveWorkbenchRendererUrl({
    rendererUrl: process.env.ELECTRON_RENDERER_URL,
    rendererFile
  });
  const window = new BrowserWindow({
    ...createMainWindowOptions(),
    title: '工作台',
    backgroundColor: '#EAFBF5',
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });

  installHostNavigationGuard(window.webContents, trustedRendererUrl);
  if (process.env.ELECTRON_RENDERER_URL) window.loadURL(trustedRendererUrl);
  else window.loadFile(rendererFile);

  // 等待渲染层完成首帧后再显示，确保固定尺寸窗口不会在首帧加载过程中闪烁。
  window.once('ready-to-show', () => {
    window.show();
  });
  return window;
}

const ownsSingleInstance = acquireSingleInstance(app);
let lifecycle: WorkbenchLifecycleController | undefined;
const cleanup = createOrderedCleanup([
  { name: 'Workbench IPC 与应用运行时', close: () => closeWorkbench?.() },
  { name: 'Workbench 主窗口', close: () => lifecycle?.destroyMainWindowForShutdown() },
  { name: '应用窗口', close: () => closeAppWindows?.() },
  { name: '应用资源协议', close: () => unregisterAppProtocol?.() },
  { name: '应用窗口状态仓储', close: () => closeAppWindowState?.() }
]);
lifecycle = ownsSingleInstance ? new WorkbenchLifecycleController({
  app,
  createMainWindow: createWindow,
  getNativeWindowCount: () => BrowserWindow.getAllWindows().length,
  cleanup,
  platform: process.platform
}) : undefined;

if (ownsSingleInstance) void app.whenReady().then(async () => {
  // Windows 菜单栏会与自绘 Desktop Shell 重复，工作台只保留统一的顶栏与窗口控制。
  Menu.setApplicationMenu(null);
  let developmentOverride;
  try {
    developmentOverride = await loadDevelopmentAppOverride({ isPackaged: app.isPackaged });
  } catch (error) {
    console.error(`本地开发应用配置错误：${error instanceof Error ? error.message : String(error)}`);
    app.quit();
    return;
  }
  let developmentOverrideEnabled = false;
  const dataDirectory = join(app.getPath('userData'), 'Workbench');
  unregisterAppProtocol = registerAppResourceProtocol(join(app.getPath('userData'), 'Workbench', 'apps'), developmentOverride, () => developmentOverrideEnabled);
  const appWindowState = new AppWindowStateRepository(join(dataDirectory, 'workbench.db'));
  closeAppWindowState = () => appWindowState.close();
  const appWindowManager = new AppWindowManager({
    stateStore: appWindowState,
    createWindow: (options) => new BrowserWindow(options),
    getDisplays: () => screen.getAllDisplays(),
    getPrimaryDisplay: () => screen.getPrimaryDisplay(),
    preloadPath: join(__dirname, '../preload/index.js'),
    rendererUrl: process.env.ELECTRON_RENDERER_URL,
    rendererFile: join(__dirname, '../renderer/index.html')
  });
  closeAppWindows = () => appWindowManager.closeAll();
  closeWorkbench = registerWorkbenchIpc(app.getPath('userData'), {
    developmentOverride,
    onDevelopmentOverrideStateChange: (enabled) => { developmentOverrideEnabled = enabled; },
    openAppWindow: async (options) => { await appWindowManager.open(options); },
    resolveAppWindow: (webContentsId) => appWindowManager.resolveWebContents(webContentsId),
    markAppWindowEventSurfaceReady: (webContentsId) => appWindowManager.markEventSurfaceReady(webContentsId),
    deliverAppWindowEvent: (appId, windowKey, event) => appWindowManager.deliverEvent(appId, windowKey, event)
  });
  lifecycle?.openMainWindow();
});
