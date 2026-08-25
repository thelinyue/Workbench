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
  });

  it('preload 和共享类型声明同步暴露应用中心 API', () => {
    expect(preloadSource).toContain('apps: {');
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:list'");
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:invoke'");
    expect(bridgeSource).toContain('apps: {');
    expect(bridgeSource).toContain('invoke(appId: string, method: string, payload?: unknown)');
  });
});
