import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';

const ipcSource = await readFile(new URL('../../src/main/ipc.ts', import.meta.url), 'utf8');
const preloadSource = await readFile(new URL('../../src/preload/index.ts', import.meta.url), 'utf8');
const bridgeSource = await readFile(new URL('../../src/shared/bridge.d.ts', import.meta.url), 'utf8');

describe('应用中心 Electron 桥接', () => {
  it('主进程注册应用列表、刷新、安装、启动和 RPC 入口', () => {
    expect(ipcSource).toContain("ipcMain.handle('apps:list'");
    expect(ipcSource).toContain("ipcMain.handle('apps:refresh-catalog'");
    expect(ipcSource).toContain("ipcMain.handle('apps:install'");
    expect(ipcSource).toContain("ipcMain.handle('apps:launch'");
    expect(ipcSource).toContain("ipcMain.handle('apps:invoke'");
    expect(ipcSource).toContain("ipcMain.handle('apps:reload'");
  });

  it('应用中心使用 Electron 网络栈下载目录和应用包', () => {
    expect(ipcSource).toContain("import { requestAppResource } from './services/app-network-request';");
    expect(ipcSource).toContain('request: requestAppResource');
  });

  it('逐项安装分析中心和 SSH 终端的内置种子资源', () => {
    expect(ipcSource).toContain("id: 'analysis-center'");
    expect(ipcSource).toContain("id: 'terminal'");
    expect(ipcSource).toContain("join(process.resourcesPath, 'apps', seed.id, `${seed.id}.zip`)");
    expect(ipcSource).toContain('const current = options.appRegistry.get(seed.id);');
    expect(ipcSource).toContain('compareAppVersions(release.version, current.activeVersion) <= 0');
  });

  it('种子安装会移除工具附带的 appId 元数据，并在单个应用失败后继续处理', () => {
    expect(ipcSource).toContain('delete release.appId;');
    expect(ipcSource).toContain('预置应用安装失败：${seed.id}');
  });

  it('preload 和共享类型声明同步暴露应用中心 API', () => {
    expect(preloadSource).toContain('apps: {');
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:list'");
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:invoke'");
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:reload'");
    expect(preloadSource).toContain("ipcRenderer.invoke('app-window:get-context')");
    expect(preloadSource).toContain("ipcRenderer.invoke('shell:is-maximized')");
    expect(bridgeSource).toContain('apps: {');
    expect(bridgeSource).toContain('invoke(appId: string, method: string, payload?: unknown)');
    expect(bridgeSource).toContain('reload(appId: string): Promise<void>');
    expect(bridgeSource).toContain('getContext(): Promise<AppWindowContext>');
    expect(bridgeSource).toContain('onMaximizedChanged(listener: (maximized: boolean) => void)');
  });

  it('开发覆盖只作用于已安装应用，并以 dev 资源地址加载', () => {
    expect(ipcSource).toContain('developmentOverride?: DevelopmentAppOverride');
    expect(ipcSource).toContain('developmentOverride: item.id === developmentOverride?.appId');
    expect(ipcSource).toContain('workbench-app://${item.id}/dev/${manifest.runtime.rendererEntry}');
    expect(ipcSource).toContain('当前应用未启用本地开发覆盖，无法重载');
    expect(ipcSource).toContain('const updateDevelopmentOverrideState = () =>');
    expect(ipcSource).toContain('updateDevelopmentOverrideState();');
  });

  it('只允许分析中心通过独立能力读取和更新官方规则', () => {
    expect(ipcSource).toContain("if (method === 'rules.getActive') return 'rules.read';");
    expect(ipcSource).toContain("if (method === 'rules.getUpdateState' || method === 'rules.updateOfficial') return 'rules.update';");
    expect(ipcSource).toContain("appId !== 'analysis-center'");
    expect(ipcSource).toContain("return result.data;");
  });

  it('目录选择器支持多选并在取消时返回空数组', () => {
    expect(ipcSource).toContain("properties: ['openDirectory', 'createDirectory', 'multiSelections']");
    expect(ipcSource).toContain('return result.canceled ? [] : result.filePaths;');
  });
});
