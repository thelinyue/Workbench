import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const electronMock = vi.hoisted(() => {
  const handlers = new Map<string, (...args: any[]) => unknown>();
  return {
    handlers,
    app: {
      getVersion: vi.fn(() => '0.1.6'),
      on: vi.fn(),
      off: vi.fn()
    },
    BrowserWindow: {
      getAllWindows: vi.fn(() => []),
      fromWebContents: vi.fn(() => undefined)
    },
    dialog: {
      showOpenDialog: vi.fn(),
      showSaveDialog: vi.fn()
    },
    ipcMain: {
      handle: vi.fn((channel: string, handler: (...args: any[]) => unknown) => handlers.set(channel, handler)),
      removeHandler: vi.fn((channel: string) => handlers.delete(channel))
    },
    shell: {
      openPath: vi.fn(async () => ''),
      showItemInFolder: vi.fn()
    }
  };
});

vi.mock('electron', () => electronMock);
vi.mock('keytar', () => ({ getPassword: vi.fn(), setPassword: vi.fn(), deletePassword: vi.fn() }));

import { AppRegistryRepository } from '../../src/main/data/app-registry-repository';
import { registerWorkbenchIpc } from '../../src/main/ipc';

const roots: string[] = [];
const cleanups: Array<() => Promise<void>> = [];

beforeEach(() => {
  Object.defineProperty(process, 'resourcesPath', { configurable: true, value: process.cwd() });
  electronMock.handlers.clear();
  electronMock.dialog.showOpenDialog.mockReset();
  electronMock.dialog.showSaveDialog.mockReset();
});

afterEach(async () => {
  await Promise.all(cleanups.splice(0).map((cleanup) => cleanup()));
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe('应用 Host 文件能力 IPC', () => {
  it('把旧版和带参数的文件选择请求交给通用选择器', async () => {
    const runtime = await createInstalledTerminal(['file.open']);
    electronMock.dialog.showOpenDialog
      .mockResolvedValueOnce({ canceled: false, filePaths: ['D:/package.tgz'] })
      .mockResolvedValueOnce({ canceled: true, filePaths: [] });
    const cleanup = registerWorkbenchIpc(runtime.userDataPath);
    cleanups.push(cleanup);
    const invoke = electronMock.handlers.get('apps:invoke')!;

    await expect(invoke({}, { appId: 'terminal', method: 'host.chooseFiles' })).resolves.toEqual(['D:/package.tgz']);
    await expect(invoke({}, {
      appId: 'terminal',
      method: 'host.chooseFiles',
      payload: { multiple: false, filters: [{ name: 'SSH 私钥', extensions: ['pem', 'key'] }] }
    })).resolves.toEqual([]);

    expect(electronMock.dialog.showOpenDialog).toHaveBeenNthCalledWith(1, {
      properties: ['openFile', 'multiSelections'],
      filters: [{ name: '诊断包', extensions: ['tgz', 'temp', 'zip'] }]
    });
    expect(electronMock.dialog.showOpenDialog).toHaveBeenNthCalledWith(2, {
      properties: ['openFile'],
      filters: [{ name: 'SSH 私钥', extensions: ['pem', 'key'] }]
    });
  });

  it('保存路径取消返回 null，且缺少 file.save 时在打开对话框前拒绝', async () => {
    const runtime = await createInstalledTerminal(['file.open', 'file.save']);
    electronMock.dialog.showSaveDialog.mockResolvedValue({ canceled: true, filePath: '' });
    const cleanup = registerWorkbenchIpc(runtime.userDataPath);
    cleanups.push(cleanup);
    const invoke = electronMock.handlers.get('apps:invoke')!;

    await expect(invoke({}, {
      appId: 'terminal', method: 'host.chooseSavePath', payload: { suggestedName: 'system.log' }
    })).resolves.toBeNull();
    expect(electronMock.dialog.showSaveDialog).toHaveBeenCalledWith({ defaultPath: 'system.log' });

    await writeManifest(runtime.installPath, ['file.open']);
    await expect(invoke({}, {
      appId: 'terminal', method: 'host.chooseSavePath', payload: { suggestedName: 'blocked.log' }
    })).rejects.toThrow('应用未获授权使用宿主能力：file.save');
    expect(electronMock.dialog.showSaveDialog).toHaveBeenCalledTimes(1);
  });
});

async function createInstalledTerminal(capabilities: string[]) {
  const userDataPath = await mkdtemp(join(tmpdir(), 'workbench-host-file-'));
  roots.push(userDataPath);
  const installPath = join(userDataPath, 'terminal-app');
  await mkdir(installPath, { recursive: true });
  await writeManifest(installPath, capabilities);
  const repository = new AppRegistryRepository(join(userDataPath, 'Workbench', 'apps.db'));
  repository.upsert({
    id: 'terminal',
    name: 'SSH 终端',
    description: '测试应用',
    publisherId: 'thelinyue',
    installedVersion: '2.0.0',
    activeVersion: '2.0.0',
    installPath,
    state: 'installed'
  });
  repository.close();
  return { userDataPath, installPath };
}

async function writeManifest(installPath: string, capabilities: string[]) {
  await writeFile(join(installPath, 'manifest.json'), JSON.stringify({
    schemaVersion: 1,
    id: 'terminal',
    name: 'SSH 终端',
    description: '测试应用',
    publisherId: 'thelinyue',
    version: '2.0.0',
    hostApiVersion: '1.0',
    minWorkbenchVersion: '0.1.6',
    runtime: { kind: 'web', rendererEntry: 'renderer/index.html', icon: 'renderer/icon.png' },
    capabilities
  }), 'utf8');
}
