import { access, readFile, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import { app, BrowserWindow, dialog, ipcMain, shell } from 'electron';
import * as keytar from 'keytar';
import { z } from 'zod';
import { DesktopLayoutRepository } from './data/desktop-layout-repository';
import { AppRegistryRepository } from './data/app-registry-repository';
import { AppCatalogClient } from './services/app-catalog-client';
import { AppCenterService } from './services/app-center-service';
import { AppPackageInstaller } from './services/app-package-installer';
import { AppRuntimeManager } from './services/app-runtime-manager';
import { parseAppCatalog, parseAppManifest } from './services/app-package-validator';
import { loadTrustedAppKeys } from './services/app-trust-store';
import { RulesService, type AnalyzerRuleCatalog } from './services/rules-service';
import officialRules from './config/official-rules.json';
import type { AppCatalogItem, AppCatalogRelease } from '../shared/app-contract';

const appIdSchema = z.string().regex(/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/);
const layoutSchema = z.array(z.object({ appId: appIdSchema, x: z.number().int().min(0).max(5000), y: z.number().int().min(0).max(5000) }));
const appInstallSchema = z.object({ appId: appIdSchema, version: z.string().optional() });
const appInvokeSchema = z.object({ appId: appIdSchema, method: z.string().min(1).max(200), payload: z.unknown().optional() });

/**
 * 注册工作台全部受控 IPC 入口。
 *
 * 每个参数均在主进程进行 Zod 校验；渲染层传来的 id 还会再次解析为仓储对象，防止页面脚本
 * 伪造任意本机路径触发文件删除、浏览器打开或资源管理器定位。
 */
export function registerWorkbenchIpc(userDataPath: string): () => void {
  const dataDirectory = join(userDataPath, 'Workbench');
  const repository = new DesktopLayoutRepository(join(dataDirectory, 'workbench.db'));
  const appRegistry = new AppRegistryRepository(join(dataDirectory, 'apps.db'));
  const appCatalog = new AppCatalogClient({
    catalogUrl: process.env.HEPHAESTUS_APP_CATALOG_URL ?? 'https://raw.githubusercontent.com/thelinyue/Workbench-Apps/main/catalog.json',
    repository: appRegistry
  });
  const trustedKeys = loadTrustedAppKeys();
  const appInstaller = new AppPackageInstaller({
    appsRoot: join(dataDirectory, 'apps'),
    workbenchVersion: app.getVersion(),
    hostApiVersion: '1.0',
    trustedKeys,
    repository: appRegistry
  });
  const appCenter = new AppCenterService({ repository: appRegistry, catalog: appCatalog, installer: appInstaller, workbenchVersion: app.getVersion(), hostApiVersion: '1.0' });
  const appRuntime = new AppRuntimeManager();
  const rulesService = new RulesService({ rootDirectory: join(dataDirectory, 'Rules'), officialRules: officialRules as AnalyzerRuleCatalog });
  const notifyRenderer = () => BrowserWindow.getAllWindows().forEach((window) => window.webContents.send('workbench:changed'));
  const seedReady = installSeedApp({ dataDirectory, appRegistry, appInstaller });

  const getInstalledApp = (input: unknown) => {
    const appId = appIdSchema.parse(input);
    const item = appRegistry.get(appId);
    if (!item?.activeVersion || !item.installPath) throw new Error(`应用尚未安装或没有可启动版本：${appId}`);
    return item;
  };
  const loadAppManifest = async (input: unknown) => {
    const item = getInstalledApp(input);
    const manifest = parseAppManifest(JSON.parse(await readFile(join(item.installPath!, 'manifest.json'), 'utf8')));
    if (manifest.id !== item.id || manifest.version !== item.activeVersion) throw new Error(`应用 manifest 与注册表不一致：${item.id}`);
    return { item, manifest };
  };
  const appHostCapability = (method: string): string | undefined => {
    if (method === 'rules.getActive') return 'rules.read';
    if (method.startsWith('rules.')) return 'rules.edit';
    if (method.startsWith('ssh.credentials.')) return 'ssh.credentials';
    return ({
    'host.chooseFiles': 'file.open',
    'host.chooseDirectory': 'file.open',
    'host.saveFile': 'file.save',
    'host.openPath': 'shell.openPath',
    'host.showItemInFolder': 'shell.showItemInFolder',
    'ssh.credentials': 'ssh.credentials'
    }[method]);
  };
  const invokeHostCapability = async (appId: string, method: string, payload: unknown): Promise<unknown> => {
    const capability = appHostCapability(method);
    if (!capability) return appRuntime.invoke(appId, method, payload);
    const { manifest } = await loadAppManifest(appId);
    if (!manifest.capabilities.includes(capability)) throw new Error(`应用未获授权使用宿主能力：${capability}`);
    if (method.startsWith('rules.')) {
      if (method === 'rules.getActive' && appId !== 'analysis-center') throw new Error('只有分析中心可以读取激活规则。');
      if (method !== 'rules.getActive' && appId !== 'log-rule-editor') throw new Error('只有规则编辑器可以修改规则。');
      const result = await rulesService.invoke(method, payload);
      return method === 'rules.getActive' ? result.data : result;
    }
    if (capability === 'ssh.credentials') {
      if (appId !== 'ssh-terminal') throw new Error('只有 SSH 终端应用可以访问 SSH 凭据库。');
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
    if (method === 'host.chooseFiles') {
      const result = await dialog.showOpenDialog({ properties: ['openFile', 'multiSelections'], filters: [{ name: '诊断包', extensions: ['tgz', 'temp', 'zip'] }] });
      return result.canceled ? [] : result.filePaths;
    }
    if (method === 'host.chooseDirectory') {
      const result = await dialog.showOpenDialog({ properties: ['openDirectory', 'createDirectory'] });
      return result.canceled ? null : result.filePaths[0] ?? null;
    }
    if (method === 'host.saveFile') {
      const value = z.object({
        fileName: z.string().min(1).max(255).refine((item) => basename(item) === item && !/[\\/:*?"<>|\0]/.test(item), '输出文件名包含不安全字符'),
        content: z.string().max(20 * 1024 * 1024, '输出内容过大'),
        overwriteRequested: z.boolean().default(false)
      }).parse(payload);
      const result = await dialog.showSaveDialog({ defaultPath: value.fileName, filters: [{ name: 'LVM VG 文件', extensions: ['vg', 'txt'] }] });
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
  };

  ipcMain.handle('desktop:load-layout', () => repository.list());
  ipcMain.handle('desktop:save-layout', (_event, input) => repository.save(layoutSchema.parse(input)));
  ipcMain.handle('shell:minimize-window', (event) => BrowserWindow.fromWebContents(event.sender)?.minimize());
  ipcMain.handle('shell:toggle-maximize-window', (event) => {
    const window = BrowserWindow.fromWebContents(event.sender);
    if (!window) return;
    if (window.isMaximized()) window.unmaximize(); else window.maximize();
  });
  ipcMain.handle('shell:close-window', (event) => BrowserWindow.fromWebContents(event.sender)?.close());
  ipcMain.handle('apps:list', async () => { await seedReady; return appCenter.list(); });
  ipcMain.handle('apps:refresh-catalog', async () => { await seedReady; const result = await appCenter.refresh(); notifyRenderer(); return result; });
  ipcMain.handle('apps:get-catalog-snapshot', () => appRegistry.loadCatalogSnapshot() ?? null);
  ipcMain.handle('apps:install', async (_event, input) => { await seedReady; const value = appInstallSchema.parse(input); const result = await appCenter.install(value.appId, value.version); notifyRenderer(); return result; });
  ipcMain.handle('apps:launch', async (_event, input) => {
    const { item, manifest } = await loadAppManifest(input);
    await appRuntime.start({ appId: item.id, installPath: item.installPath!, dataDirectory: join(dataDirectory, 'apps', item.id, 'data'), manifest });
    notifyRenderer();
  });
  ipcMain.handle('apps:get-entry-url', async (_event, input) => {
    const { item, manifest } = await loadAppManifest(input);
    return `workbench-app://${item.id}/${manifest.version}/${manifest.runtime.rendererEntry}`;
  });
  ipcMain.handle('apps:invoke', (_event, input) => { const value = appInvokeSchema.parse(input); return invokeHostCapability(value.appId, value.method, value.payload); });
  appRuntime.onEvent((event) => {
    if (event.event === 'runtime.failed') {
      const current = appRegistry.get(event.appId);
      const payload = event.payload as { message?: unknown };
      if (current) appRegistry.upsert({ ...current, state: 'broken', errorMessage: typeof payload.message === 'string' ? payload.message : '应用 Worker 已停止' });
    }
    BrowserWindow.getAllWindows().forEach((window) => window.webContents.send('workbench:app-event', event));
  });
  return () => {
    void Promise.all(appRegistry.list().map((item) => appRuntime.stop(item.id)));
    appRegistry.close();
    repository.close();
  };
}

interface SeedInstallOptions {
  dataDirectory: string;
  appRegistry: AppRegistryRepository;
  appInstaller: AppPackageInstaller;
}

/** 首次启动时安装安装包内的官方种子应用；安装仍经过与在线包相同的哈希、签名和 ZIP 安全校验。 */
async function installSeedApp(options: SeedInstallOptions): Promise<void> {
  if (options.appRegistry.get('analysis-center')) return;
  const candidates = [
    join(process.resourcesPath, 'apps', 'analysis-center.zip'),
    join(process.cwd(), 'build', 'seed-app', 'analysis-center.zip')
  ];
  const releaseCandidates = [
    join(process.resourcesPath, 'apps', 'release.json'),
    join(process.cwd(), 'build', 'seed-app', 'release.json')
  ];
  const packagePath = await firstExisting(candidates);
  const releasePath = await firstExisting(releaseCandidates);
  if (!packagePath || !releasePath) return;
  try {
    const release = JSON.parse(await readFile(releasePath, 'utf8')) as AppCatalogRelease & { url?: string };
    const app: AppCatalogItem = {
      id: 'analysis-center',
      name: '分析中心',
      description: '诊断包导入、监控扫描、综合分析、存储健康分析和离线报告',
      publisherId: 'thelinyue',
      releases: [{ ...release, url: release.url ?? 'https://github.com/thelinyue/Workbench-Apps/releases/download/analysis-center-v1.0.0/analysis-center-v1.0.0.zip' }]
    };
    parseAppCatalog({ schemaVersion: 1, apps: [app] });
    const payload = await readFile(packagePath);
    await options.appInstaller.installRelease(app, app.releases[0]!, payload);
  } catch (error) {
    console.error(`预置应用安装失败：${error instanceof Error ? error.message : String(error)}`);
  }
}

async function firstExisting(paths: string[]): Promise<string | undefined> {
  for (const path of paths) {
    try { await access(path); return path; } catch { /* 继续检查下一个候选路径。 */ }
  }
  return undefined;
}
