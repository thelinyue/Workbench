import { access, readFile, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { app, BrowserWindow, clipboard, dialog, ipcMain, Notification, shell } from 'electron';
import * as keytar from 'keytar';
import { z } from 'zod';
import { DesktopLayoutRepository } from './data/desktop-layout-repository';
import { AppRegistryRepository } from './data/app-registry-repository';
import { AppCatalogClient } from './services/app-catalog-client';
import { requestAppResource } from './services/app-network-request';
import { AppCenterService } from './services/app-center-service';
import { AppPackageInstaller } from './services/app-package-installer';
import { AppRuntimeManager } from './services/app-runtime-manager';
import { AppLifecycleCoordinator } from './services/app-lifecycle-coordinator';
import type { AppResolvedApp } from './services/app-lifecycle-coordinator';
import { AppPackageUninstaller } from './services/app-package-uninstaller';
import { launchAppFromIpc } from './services/app-launch-coordinator';
import type { AppWindowOpenOptions } from './services/app-window-manager';
import { compareAppVersions, parseAppCatalog, parseAppManifest } from './services/app-package-validator';
import { reloadDevelopmentAppOverride, type DevelopmentAppOverride } from './services/app-development-override';
import { createAppWindowContextHandler, createAppWindowEventReadyHandler } from './services/app-window-context-ipc';
import { AppNotificationManager } from './services/app-notification-manager';
import { chooseAppFiles, chooseAppSavePath } from './services/app-file-dialog';
import { loadTrustedAppKeys } from './services/app-trust-store';
import { registerWindowShellIpc } from './services/window-shell-ipc';
import { WorkbenchIpcBoundary, createWorkbenchIpcCleanup } from './services/workbench-ipc-shutdown';
import { RulesService, type AnalyzerRuleCatalog } from './services/rules-service';
import officialRules from './config/official-rules.json';
import type { AppCatalogItem, AppCatalogRelease, AppHostEvent } from '../shared/app-contract';

const appIdSchema = z.string().regex(/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/);
const layoutSchema = z.array(z.object({ appId: appIdSchema, x: z.number().int().min(0).max(5000), y: z.number().int().min(0).max(5000) }));
const appInstallSchema = z.object({ appId: appIdSchema, version: z.string().optional() });
const appEnabledSchema = z.object({ appId: appIdSchema, enabled: z.boolean() });
const appUninstallSchema = z.object({ appId: appIdSchema, deleteData: z.boolean() });
const appInvokeSchema = z.object({ appId: appIdSchema, method: z.string().min(1).max(200), payload: z.unknown().optional() });
const MAX_CLIPBOARD_TEXT_BYTES = 1024 * 1024;

/**
 * 核心功能应用随 Workbench 安装包提供签名种子包，首次启动无需联网即可安装。
 * 该清单只描述壳层的内置职责；应用版本与运行能力仍完全由签名 Release 和 manifest 决定，
 * 因而应用中心后续可像普通应用一样独立更新。
 */
const CORE_SEED_APPS = [
  { id: 'analysis-center', name: '分析中心', description: '诊断包导入、监控扫描、综合分析、存储健康分析和离线报告', publisherId: 'thelinyue' },
  { id: 'terminal', name: 'SSH 终端', description: '保存连接、使用密码或私钥安全登录远程主机，并在多标签终端中进行运维操作', publisherId: 'thelinyue' }
] as const;

/**
 * 注册工作台全部受控 IPC 入口。
 *
 * 每个参数均在主进程进行 Zod 校验；渲染层传来的 id 还会再次解析为仓储对象，防止页面脚本
 * 伪造任意本机路径触发文件删除、浏览器打开或资源管理器定位。
 */
export interface RegisterWorkbenchIpcOptions {
  developmentOverride?: DevelopmentAppOverride;
  onDevelopmentOverrideStateChange?: (enabled: boolean) => void;
  openAppWindow?: (options: AppWindowOpenOptions) => void | Promise<void>;
  resolveAppWindow?: (webContentsId: number) => Readonly<{ appId: string; windowKey: string }> | undefined;
  markAppWindowEventSurfaceReady?: (webContentsId: number) => void;
  deliverAppWindowEvent?: (appId: string, windowKey: string, event: AppHostEvent) => void;
  closeAppWindow?: (appId: string) => Promise<void>;
}

export function registerWorkbenchIpc(userDataPath: string, options: RegisterWorkbenchIpcOptions = {}): () => Promise<void> {
  const { developmentOverride } = options;
  const unregisterWindowShellIpc = registerWindowShellIpc();
  const ipcBoundary = new WorkbenchIpcBoundary(ipcMain);
  const dataDirectory = join(userDataPath, 'Workbench');
  const repository = new DesktopLayoutRepository(join(dataDirectory, 'workbench.db'));
  const appRegistry = new AppRegistryRepository(join(dataDirectory, 'apps.db'));
  const appCatalog = new AppCatalogClient({
    catalogUrl: process.env.HEPHAESTUS_APP_CATALOG_URL ?? 'https://raw.githubusercontent.com/thelinyue/Workbench-Apps/main/catalog.json',
    repository: appRegistry,
    request: requestAppResource
  });
  const trustedKeys = loadTrustedAppKeys();
  const appInstaller = new AppPackageInstaller({
    appsRoot: join(dataDirectory, 'apps'),
    workbenchVersion: app.getVersion(),
    hostApiVersion: '1.1',
    trustedKeys,
    repository: appRegistry
  });
  const appRuntime = new AppRuntimeManager();
  const appCenter = new AppCenterService({
    repository: appRegistry,
    catalog: appCatalog,
    installer: appInstaller,
    workbenchVersion: app.getVersion(),
    hostApiVersion: '1.1',
    runtimeState: (appId) => appRuntime.getState(appId),
    builtInAppIds: CORE_SEED_APPS.map((seed) => seed.id)
  });
  const appUninstaller = new AppPackageUninstaller({ appsRoot: join(dataDirectory, 'apps') });
  const rulesService = new RulesService({
    rootDirectory: join(dataDirectory, 'Rules'),
    officialRules: officialRules as AnalyzerRuleCatalog,
    catalogUrl: process.env.HEPHAESTUS_ANALYSIS_RULES_CATALOG_URL ?? 'https://raw.githubusercontent.com/thelinyue/Hephaestus-Workbench-Plugins/main/rules/analysis-center-rules/catalog.json',
    trustedKeys
  });
  const notifyRenderer = () => BrowserWindow.getAllWindows().forEach((window) => window.webContents.send('workbench:changed'));
  const updateDevelopmentOverrideState = () => {
    const enabled = Boolean(developmentOverride && appRegistry.get(developmentOverride.appId)?.enabled && appRegistry.get(developmentOverride.appId)?.activeVersion);
    options.onDevelopmentOverrideStateChange?.(enabled);
    return enabled;
  };
  const seedInstallReady = installCoreSeedApps({ dataDirectory, appRegistry, appInstaller });

  const getInstalledApp = (input: unknown) => {
    const appId = appIdSchema.parse(input);
    const item = appRegistry.get(appId);
    if (!item?.activeVersion || !item.installPath) throw new Error(`应用尚未安装或没有可启动版本：${appId}`);
    return item;
  };
  const loadAppManifest = async (input: unknown) => {
    const item = getInstalledApp(input);
    const isDevelopmentOverride = item.id === developmentOverride?.appId;
    const currentDevelopmentOverride = isDevelopmentOverride ? await reloadDevelopmentAppOverride(developmentOverride!) : undefined;
    const installPath = currentDevelopmentOverride?.installPath ?? item.installPath!;
    const manifest = isDevelopmentOverride
      ? currentDevelopmentOverride!.manifest
      : parseAppManifest(JSON.parse(await readFile(join(installPath, 'manifest.json'), 'utf8')));
    if (!isDevelopmentOverride && (manifest.id !== item.id || manifest.version !== item.activeVersion)) throw new Error(`应用 manifest 与注册表不一致：${item.id}`);
    return { item, installPath, manifest, isDevelopmentOverride };
  };
  const resolveApp = async (appId: string): Promise<AppResolvedApp> => {
    const { item, installPath, manifest } = await loadAppManifest(appId);
    return {
      record: item,
      installPath,
      dataDirectory: join(dataDirectory, 'apps', item.id, 'data'),
      manifest
    };
  };
  const runtimeAdapter = {
    start: (resolved: AppResolvedApp) => {
      ipcBoundary.ensureOpen();
      // 协调器的解析对象包含 registry record；传给 runtime 时显式收窄为运行时稳定契约，避免路径由 renderer 进入。
      return appRuntime.start({
        appId: resolved.record.id,
        installPath: resolved.installPath,
        dataDirectory: resolved.dataDirectory,
        manifest: resolved.manifest
      });
    },
    stop: (appId: string) => appRuntime.stop(appId),
    getState: (appId: string) => appRuntime.getState(appId)
  };
  const lifecycleCoordinator = new AppLifecycleCoordinator({
    repository: appRegistry,
    runtimeManager: runtimeAdapter,
    windowManager: { closeApp: options.closeAppWindow ?? (async () => undefined) },
    uninstaller: appUninstaller,
    resolveApp,
    seedAppIds: CORE_SEED_APPS.map((seed) => seed.id)
  });
  const withDevelopmentOverride = (item: ReturnType<AppRegistryRepository['list']>[number]) => item.activeVersion
    ? { ...item, developmentOverride: item.id === developmentOverride?.appId && item.enabled }
    : item;
  const appHostCapability = (method: string): string | undefined => {
    if (method === 'rules.getActive') return 'rules.read';
    if (method === 'rules.getUpdateState' || method === 'rules.updateOfficial') return 'rules.update';
    if (method.startsWith('rules.')) return 'rules.edit';
    if (method.startsWith('ssh.credentials.')) return 'ssh.credentials';
    return ({
    'host.chooseFiles': 'file.open',
    'host.chooseDirectory': 'file.open',
    'host.chooseSavePath': 'file.save',
    'host.saveFile': 'file.save',
    'host.clipboard.readText': 'clipboard.read',
    'host.clipboard.writeText': 'clipboard.write',
    'host.openPath': 'shell.openPath',
    'host.showItemInFolder': 'shell.showItemInFolder',
    'ssh.credentials': 'ssh.credentials'
    }[method]);
  };
  const invokeHostCapability = (appId: string, method: string, payload: unknown): Promise<unknown> => lifecycleCoordinator.runEnabled(appId, async ({ record, manifest }) => {
    const capability = appHostCapability(method);
    if (!capability) return appRuntime.invoke(record.id, method, payload);
    if (!manifest.capabilities.includes(capability)) throw new Error(`应用未获授权使用宿主能力：${capability}`);
    if (method.startsWith('rules.')) {
      const analysisCenterMethod = method === 'rules.getActive' || method === 'rules.getUpdateState' || method === 'rules.updateOfficial';
      if (analysisCenterMethod && appId !== 'analysis-center') throw new Error('只有分析中心可以读取或更新官方规则。');
      if (!analysisCenterMethod && appId !== 'log-rule-editor') throw new Error('只有规则编辑器可以修改本地规则。');
      const result = await rulesService.invoke(method, payload);
      if (analysisCenterMethod) return result.data;
      return result;
    }
    if (capability === 'ssh.credentials') {
      if (appId !== 'terminal') throw new Error('只有 SSH 终端应用可以访问 SSH 凭据库。');
      const value = z.object({ credentialId: z.string().uuid(), username: z.string().min(1).max(256).optional(), secret: z.string().min(1).max(16 * 1024).optional() }).parse(payload);
      const service = 'com.thelinyue.hephaestus-workbench.ssh';
      if (method === 'ssh.credentials.read') return keytar.getPassword(service, value.credentialId);
      if (method === 'ssh.credentials.write') {
        if (!value.secret) throw new Error('保存 SSH 凭据时缺少密钥或密码。');
        await keytar.setPassword(service, value.credentialId, value.secret);
        return undefined;
      }
      if (method === 'ssh.credentials.delete') {
        await keytar.deletePassword(service, value.credentialId);
        return undefined;
      }
      throw new Error(`不支持的 SSH 凭据请求：${method}`);
    }
    if (method === 'host.clipboard.readText') {
      z.undefined().parse(payload);
      const text = clipboard.readText();
      if (Buffer.byteLength(text, 'utf8') > MAX_CLIPBOARD_TEXT_BYTES) throw new Error('剪贴板文本超过 1 MiB，无法粘贴到终端。');
      return text;
    }
    if (method === 'host.clipboard.writeText') {
      const value = z.object({ text: z.string() }).parse(payload);
      if (Buffer.byteLength(value.text, 'utf8') > MAX_CLIPBOARD_TEXT_BYTES) throw new Error('选中的终端文本超过 1 MiB，无法复制到剪贴板。');
      clipboard.writeText(value.text);
      return undefined;
    }
    if (method === 'host.chooseFiles') {
      return chooseAppFiles({ showOpenDialog: (dialogOptions) => dialog.showOpenDialog(dialogOptions as Electron.OpenDialogOptions) }, payload);
    }
    if (method === 'host.chooseDirectory') {
      // 分析中心可累计监控多个目录，取消选择统一返回空数组，避免 renderer 处理 null 分支。
      const result = await dialog.showOpenDialog({ properties: ['openDirectory', 'createDirectory', 'multiSelections'] });
      return result.canceled ? [] : result.filePaths;
    }
    if (method === 'host.chooseSavePath') {
      return chooseAppSavePath({ showSaveDialog: (dialogOptions) => dialog.showSaveDialog(dialogOptions) }, payload);
    }
    if (method === 'host.saveFile') {
      const value = z.object({
        fileName: z.string().min(1).max(255).refine((item) => basename(item) === item && !/[\\/:*?"<>|\0]/.test(item), '输出文件名包含不安全字符'),
        content: z.string().max(20 * 1024 * 1024, '输出内容过大'),
        kind: z.enum(['lvm-vg', 'html']).default('lvm-vg'),
        overwriteRequested: z.boolean().default(false)
      }).parse(payload);
      const result = await dialog.showSaveDialog({ defaultPath: value.fileName, filters: [value.kind === 'html' ? { name: 'HTML 文件', extensions: ['html', 'htm'] } : { name: 'LVM VG 文件', extensions: ['vg', 'txt'] }] });
      if (result.canceled || !result.filePath) return null;
      if (!value.overwriteRequested) {
        try { await access(result.filePath); throw new Error('目标文件已存在，请勾选允许覆盖后重试。'); } catch (error) {
          if (error instanceof Error && error.message.includes('目标文件已存在')) throw error;
        }
      }
      await writeFile(result.filePath, value.content, 'utf8');
      return { path: result.filePath };
    }
    const value = z.object({ path: z.string().min(1).max(4096) }).parse(payload);
    if (method === 'host.openPath') {
      const error = await shell.openPath(value.path);
      if (error) throw new Error(`无法打开文件：${error}`);
      return undefined;
    }
    shell.showItemInFolder(value.path);
    return undefined;
  });

  const launchInstalledApp = (input: unknown) => {
    const appId = appIdSchema.parse(input);
    return lifecycleCoordinator.runEnabled(appId, async (resolved) => {
      const result = await launchAppFromIpc({
        appId: resolved.record.id,
        name: resolved.manifest.name,
        manifest: resolved.manifest,
        startRuntime: () => runtimeAdapter.start(resolved),
        openAppWindow: async (windowOptions) => {
          if (!options.openAppWindow) throw new Error('应用声明了原生窗口，但主进程未配置应用窗口宿主。');
          await options.openAppWindow(windowOptions);
        }
      });
      notifyRenderer();
      return result;
    });
  };
  const notificationManager = new AppNotificationManager({
    isSupported: () => Notification.isSupported(),
    createNotification: (notificationOptions) => new Notification(notificationOptions),
    activate: async ({ appId, windowKey, event }) => {
      const result = await launchInstalledApp(appId);
      if (result.presentation !== 'app-window' || !options.deliverAppWindowEvent) throw new Error('应用没有可接收通知激活事件的原生窗口');
      options.deliverAppWindowEvent(appId, windowKey, event);
    }
  });

  // 先注册 runtime 事件/通知监听，再等待种子安装；这样冷启动 Worker 的首个事件不会丢失。
  const unregisterRuntimeEvents = appRuntime.onEvent((event) => {
    if (event.event === 'runtime.failed') {
      const current = appRegistry.get(event.appId);
      const payload = event.payload as { message?: unknown };
      if (current) appRegistry.upsert({ ...current, state: 'broken', errorMessage: typeof payload.message === 'string' ? payload.message : '应用 Worker 已停止' });
    }
    BrowserWindow.getAllWindows().forEach((window) => window.webContents.send('workbench:app-event', event));
  });
  const unregisterRuntimeNotifications = appRuntime.onNotification(({ appId, payload }) => notificationManager.show(appId, payload));
  const initialization = seedInstallReady.then(async () => {
    if (developmentOverride && !updateDevelopmentOverrideState()) console.error(`本地开发应用尚未安装或未启用，已禁用本地覆盖：${developmentOverride.appId}。`);
    await lifecycleCoordinator.startEnabledApps();
  });

  ipcMain.handle('desktop:initialize-layout', ipcBoundary.handler('desktop:initialize-layout', (_event, input) => repository.initializeDefaultLayout(layoutSchema.parse(input))));
  ipcMain.handle('desktop:save-layout', ipcBoundary.handler('desktop:save-layout', (_event, input) => repository.save(layoutSchema.parse(input))));
  ipcMain.handle('app-window:get-context', ipcBoundary.handler('app-window:get-context', createAppWindowContextHandler({
    resolveWebContents: (webContentsId) => options.resolveAppWindow?.(webContentsId),
    loadManifest: async (appId) => {
      await initialization;
      return lifecycleCoordinator.runEnabled(appId, async (resolved) => ({
        manifest: resolved.manifest,
        developmentOverride: resolved.record.id === developmentOverride?.appId
      }));
    }
  })));
  ipcMain.handle('app-window:event-surface-ready', ipcBoundary.handler('app-window:event-surface-ready', createAppWindowEventReadyHandler((webContentsId) => {
    if (!options.markAppWindowEventSurfaceReady) throw new Error('主进程未配置应用窗口事件表面。');
    options.markAppWindowEventSurfaceReady(webContentsId);
  })));
  ipcMain.handle('apps:list', ipcBoundary.handler('apps:list', async () => { await initialization; return appCenter.list().map(withDevelopmentOverride); }));
  ipcMain.handle('apps:refresh-catalog', ipcBoundary.handler('apps:refresh-catalog', async () => { await initialization; const result = await appCenter.refresh(); notifyRenderer(); return result.map(withDevelopmentOverride); }));
  ipcMain.handle('apps:get-catalog-snapshot', ipcBoundary.handler('apps:get-catalog-snapshot', () => appRegistry.loadCatalogSnapshot() ?? null));
  ipcMain.handle('apps:install', ipcBoundary.handler('apps:install', async (_event, input) => {
    await initialization;
    const value = appInstallSchema.parse(input);
    const result = await lifecycleCoordinator.install(value.appId, async () => {
      const wasUpdate = Boolean(appRegistry.get(value.appId)?.activeVersion);
      const installed = await appCenter.install(value.appId, value.version);
      return { result: installed, wasUpdate };
    });
    updateDevelopmentOverrideState();
    notifyRenderer();
    return appCenter.getItem(value.appId) ?? result;
  }));
  ipcMain.handle('apps:set-enabled', ipcBoundary.handler('apps:set-enabled', async (_event, input) => {
    await initialization;
    const value = appEnabledSchema.parse(input);
    const result = await lifecycleCoordinator.setEnabled(value.appId, value.enabled);
    notifyRenderer();
    return appCenter.getItem(value.appId) ?? result;
  }));
  ipcMain.handle('apps:uninstall', ipcBoundary.handler('apps:uninstall', async (_event, input) => {
    const value = appUninstallSchema.parse(input);
    if (CORE_SEED_APPS.some((seed) => seed.id === value.appId)) throw new Error(`内置种子应用不可卸载：${value.appId}`);
    await initialization;
    await lifecycleCoordinator.uninstall(value.appId, value.deleteData);
    notifyRenderer();
  }));
  ipcMain.handle('apps:launch', ipcBoundary.handler('apps:launch', async (_event, input) => { await initialization; return launchInstalledApp(input); }));
  ipcMain.handle('apps:get-entry-url', ipcBoundary.handler('apps:get-entry-url', async (_event, input) => {
    await initialization;
    const appId = appIdSchema.parse(input);
    return lifecycleCoordinator.runEnabled(appId, async ({ record: item, manifest }) => item.id === developmentOverride?.appId
      ? `workbench-app://${item.id}/dev/${manifest.runtime.rendererEntry}`
      : `workbench-app://${item.id}/${manifest.version}/${manifest.runtime.rendererEntry}`);
  }));
  ipcMain.handle('apps:reload', ipcBoundary.handler('apps:reload', async (_event, input) => {
    await initialization;
    const appId = appIdSchema.parse(input);
    await lifecycleCoordinator.runEnabled(appId, async (resolved) => {
      if (resolved.record.id !== developmentOverride?.appId) throw new Error('当前应用未启用本地开发覆盖，无法重载。');
      await runtimeAdapter.stop(resolved.record.id);
      await runtimeAdapter.start(resolved);
    });
    notifyRenderer();
  }));
  ipcMain.handle('apps:invoke', ipcBoundary.handler('apps:invoke', async (_event, input) => { await initialization; const value = appInvokeSchema.parse(input); return invokeHostCapability(value.appId, value.method, value.payload); }));
  return createWorkbenchIpcCleanup({
    ipcBoundary,
    unregisterWindowShellIpc,
    unregisterRuntimeEvents: () => { unregisterRuntimeEvents(); unregisterRuntimeNotifications(); },
    waitForInitialization: () => initialization,
    stopAllRuntimes: () => appRuntime.stopAll(),
    closeAppRegistry: () => appRegistry.close(),
    closeDesktopRepository: () => repository.close()
  });
}

interface SeedInstallOptions {
  dataDirectory: string;
  appRegistry: AppRegistryRepository;
  appInstaller: AppPackageInstaller;
}

/**
 * 启动时导入安装包内版本更高的核心应用。
 * 每次升级仍使用在线安装相同的哈希、签名和 ZIP 校验，应用私有数据目录不会被替换。
 */
async function installCoreSeedApps(options: SeedInstallOptions): Promise<void> {
  for (const seed of CORE_SEED_APPS) {
    const packagePath = await firstExisting([
      join(process.resourcesPath, 'apps', seed.id, `${seed.id}.zip`),
      join(process.cwd(), 'build', 'seed-app', seed.id, `${seed.id}.zip`)
    ]);
    const releasePath = await firstExisting([
      join(process.resourcesPath, 'apps', seed.id, 'release.json'),
      join(process.cwd(), 'build', 'seed-app', seed.id, 'release.json')
    ]);
    if (!packagePath || !releasePath) {
      console.error(`内置核心应用资源缺失，跳过安装：${seed.id}。`);
      continue;
    }
    try {
      const release = JSON.parse(await readFile(releasePath, 'utf8')) as AppCatalogRelease & { appId?: string };
      // 构建工具为追踪种子来源附加 appId，安装前必须恢复为严格的目录 Release 结构。
      delete release.appId;
      const current = options.appRegistry.get(seed.id);
      if (current?.activeVersion && compareAppVersions(release.version, current.activeVersion) <= 0) continue;
      const app: AppCatalogItem = { ...seed, releases: [release] };
      parseAppCatalog({ schemaVersion: 1, apps: [app] });
      await options.appInstaller.installRelease(app, app.releases[0]!, await readFile(packagePath));
    } catch (error) {
      console.error(`预置应用安装失败：${seed.id}，${error instanceof Error ? error.message : String(error)}`);
    }
  }
}

async function firstExisting(paths: string[]): Promise<string | undefined> {
  for (const path of paths) {
    try { await access(path); return path; } catch { /* 继续检查下一个候选路径。 */ }
  }
  return undefined;
}
