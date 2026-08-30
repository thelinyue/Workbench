import { mkdir, mkdtemp, rm, writeFile, access } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { launchAppFromIpc } from '../../src/main/services/app-launch-coordinator';
import { AppCenterService, type AppCenterItem } from '../../src/main/services/app-center-service';
import type { AppManifestV1 } from '../../src/shared/app-contract';
import type { DevelopmentAppOverride } from '../../src/main/services/app-development-override';

const electronMock = vi.hoisted(() => {
  const handlers = new Map<string, (...args: any[]) => unknown>();
  return {
    handlers,
    app: { getVersion: vi.fn(() => '0.1.6'), on: vi.fn(), off: vi.fn() },
    BrowserWindow: { getAllWindows: vi.fn(() => [] as any[]), fromWebContents: vi.fn(() => undefined) },
    dialog: { showOpenDialog: vi.fn(), showSaveDialog: vi.fn() },
    clipboard: { readText: vi.fn(), writeText: vi.fn() },
    ipcMain: {
      handle: vi.fn((channel: string, handler: (...args: any[]) => unknown) => handlers.set(channel, handler)),
      removeHandler: vi.fn((channel: string) => handlers.delete(channel))
    },
    Notification: { isSupported: vi.fn(() => false) },
    shell: { openPath: vi.fn(async () => ''), showItemInFolder: vi.fn() }
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
  electronMock.BrowserWindow.getAllWindows.mockReset();
  electronMock.BrowserWindow.getAllWindows.mockReturnValue([]);
});

afterEach(async () => {
  await Promise.all(cleanups.splice(0).map((cleanup) => cleanup()));
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

const baseManifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'terminal',
  name: 'SSH 终端',
  description: '终端',
  publisherId: 'thelinyue',
  version: '1.0.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.0',
  runtime: { kind: 'web', rendererEntry: 'index.html', icon: 'icon.svg' },
  capabilities: []
};

describe('应用启动 IPC 协调', () => {
  it('有 window 声明时先启动运行时，再打开原生窗口并返回 app-window', async () => {
    const calls: string[] = [];
    const manifest: AppManifestV1 = {
      ...baseManifest,
      id: 'analysis-center',
      name: '分析中心',
      window: { defaultSize: { width: 1200, height: 800 }, minSize: { width: 800, height: 560 } }
    };

    const result = await launchAppFromIpc({
      appId: manifest.id,
      name: manifest.name,
      manifest,
      startRuntime: async () => { calls.push('runtime'); },
      openAppWindow: () => { calls.push('window'); }
    });

    expect(calls).toEqual(['runtime', 'window']);
    expect(result).toEqual({ presentation: 'app-window' });
  });

  it('旧 manifest 只启动运行时并返回 embedded', async () => {
    let opened = false;

    const result = await launchAppFromIpc({
      appId: baseManifest.id,
      name: baseManifest.name,
      manifest: baseManifest,
      startRuntime: async () => undefined,
      openAppWindow: () => { opened = true; }
    });

    expect(opened).toBe(false);
    expect(result).toEqual({ presentation: 'embedded' });
  });

  it('disabled 应用的 launch、get-entry-url 和 invoke 都经过主进程入口拒绝', async () => {
    const root = await createRegisteredApp(false);
    cleanups.push(registerWorkbenchIpc(root));

    await expect(electronMock.handlers.get('apps:launch')!({}, 'demo-app')).rejects.toThrow('应用已停用：demo-app');
    await expect(electronMock.handlers.get('apps:get-entry-url')!({}, 'demo-app')).rejects.toThrow('应用已停用：demo-app');
    await expect(electronMock.handlers.get('apps:invoke')!({}, { appId: 'demo-app', method: 'packages.list' })).rejects.toThrow('应用已停用：demo-app');
  });

  it('set-enabled 和 uninstall 使用 Zod 校验并通过协调器改变运行时与安装目录', async () => {
    const root = await createRegisteredApp(true);
    cleanups.push(registerWorkbenchIpc(root));
    const setEnabled = electronMock.handlers.get('apps:set-enabled')!;
    const uninstall = electronMock.handlers.get('apps:uninstall')!;
    const list = electronMock.handlers.get('apps:list')!;

    await expect(list({})).resolves.toEqual([expect.objectContaining({ id: 'demo-app', enabled: true, runtimeState: 'running' })]);

    await expect(setEnabled({}, { appId: 'demo-app', enabled: 'false' })).rejects.toThrow();
    const disabled = await setEnabled({}, { appId: 'demo-app', enabled: false });
    expect(disabled).toMatchObject({ id: 'demo-app', enabled: false, runtimeState: 'stopped', builtIn: false });
    await expect(uninstall({}, { appId: 'demo-app', deleteData: 'yes' })).rejects.toThrow();
    await expect(uninstall({}, { appId: 'demo-app', deleteData: true })).resolves.toBeUndefined();
    await expect(access(join(root, 'Workbench', 'apps', 'demo-app'))).rejects.toMatchObject({ code: 'ENOENT' });
  });

  it('开发覆盖应用停用后刷新覆盖状态为 false 并广播 renderer', async () => {
    const root = await createRegisteredApp(true);
    const stateChanges: boolean[] = [];
    const renderer = createRendererWindow();
    cleanups.push(registerWorkbenchIpc(root, {
      developmentOverride: createDevelopmentOverride(root),
      onDevelopmentOverrideStateChange: (enabled) => stateChanges.push(enabled)
    }));
    const list = electronMock.handlers.get('apps:list')!;
    const setEnabled = electronMock.handlers.get('apps:set-enabled')!;

    await list({});
    stateChanges.length = 0;
    renderer.webContents.send.mockClear();

    await expect(setEnabled({}, { appId: 'demo-app', enabled: false })).resolves.toMatchObject({ enabled: false });

    expect(stateChanges).toEqual([false]);
    expect(renderer.webContents.send).toHaveBeenCalledWith('workbench:changed');
  });

  it('停用关闭窗口失败但 enabled 已落库时仍刷新覆盖状态并广播 renderer', async () => {
    const root = await createRegisteredApp(true);
    const stateChanges: boolean[] = [];
    const renderer = createRendererWindow();
    cleanups.push(registerWorkbenchIpc(root, {
      developmentOverride: createDevelopmentOverride(root),
      onDevelopmentOverrideStateChange: (enabled) => stateChanges.push(enabled),
      closeAppWindow: async () => { throw new Error('窗口关闭失败'); }
    }));
    const list = electronMock.handlers.get('apps:list')!;
    const setEnabled = electronMock.handlers.get('apps:set-enabled')!;

    await list({});
    stateChanges.length = 0;
    renderer.webContents.send.mockClear();

    await expect(setEnabled({}, { appId: 'demo-app', enabled: false })).rejects.toThrow('停用应用失败（demo-app）');

    expect(stateChanges).toEqual([false]);
    expect(renderer.webContents.send).toHaveBeenCalledWith('workbench:changed');
  });

  it('停用关闭窗口失败且单个 renderer 广播失败时保留生命周期错误并继续通知其他窗口', async () => {
    const root = await createRegisteredApp(true);
    const stateChanges: boolean[] = [];
    const firstRenderer = createRendererWindow();
    const secondRenderer = createRendererWindow();
    electronMock.BrowserWindow.getAllWindows.mockReturnValue([firstRenderer, secondRenderer]);
    firstRenderer.webContents.send.mockImplementation(() => { throw new Error('窗口已销毁'); });
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      cleanups.push(registerWorkbenchIpc(root, {
        developmentOverride: createDevelopmentOverride(root),
        onDevelopmentOverrideStateChange: (enabled) => stateChanges.push(enabled),
        closeAppWindow: async () => { throw new Error('窗口关闭失败'); }
      }));
      const list = electronMock.handlers.get('apps:list')!;
      const setEnabled = electronMock.handlers.get('apps:set-enabled')!;

      await list({});
      stateChanges.length = 0;
      firstRenderer.webContents.send.mockClear();
      secondRenderer.webContents.send.mockClear();

      const failure = setEnabled({}, { appId: 'demo-app', enabled: false });
      await expect(failure).rejects.toThrow('停用应用失败（demo-app）');
      await expect(failure).rejects.not.toThrow('窗口已销毁');

      expect(stateChanges).toEqual([false]);
      expect(secondRenderer.webContents.send).toHaveBeenCalledWith('workbench:changed');
      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('向渲染器广播工作台变更失败'));
      expect(errorSpy).toHaveBeenCalledWith(expect.stringContaining('窗口已销毁'));
    } finally {
      errorSpy.mockRestore();
    }
  });

  it('卸载收口失败但已禁用应用时仍刷新覆盖状态并广播 renderer', async () => {
    const root = await createRegisteredApp(true);
    const stateChanges: boolean[] = [];
    const renderer = createRendererWindow();
    cleanups.push(registerWorkbenchIpc(root, {
      developmentOverride: createDevelopmentOverride(root),
      onDevelopmentOverrideStateChange: (enabled) => stateChanges.push(enabled),
      closeAppWindow: async () => { throw new Error('窗口关闭失败'); }
    }));
    const list = electronMock.handlers.get('apps:list')!;
    const uninstall = electronMock.handlers.get('apps:uninstall')!;

    await list({});
    stateChanges.length = 0;
    renderer.webContents.send.mockClear();

    await expect(uninstall({}, { appId: 'demo-app', deleteData: false })).rejects.toThrow('卸载应用失败（demo-app）');

    expect(stateChanges).toEqual([false]);
    expect(renderer.webContents.send).toHaveBeenCalledWith('workbench:changed');
  });

  it('安装下载未完成时不会让同一应用的停用抢先落库', async () => {
    const root = await createRegisteredApp(true);
    const installGate = deferred<void>();
    let installStarted = false;
    const installed: AppCenterItem = {
      id: 'demo-app',
      name: '演示应用',
      description: '测试应用',
      publisherId: 'test',
      installedVersion: '1.0.0',
      activeVersion: '1.0.0',
      installPath: join(root, 'Workbench', 'apps', 'demo-app', '1.0.0'),
      enabled: true,
      state: 'installed',
      builtIn: false,
      runtimeState: 'running'
    };
    const installSpy = vi.spyOn(AppCenterService.prototype, 'install').mockImplementation(async () => {
      installStarted = true;
      await installGate.promise;
      return installed;
    });
    try {
      cleanups.push(registerWorkbenchIpc(root));
      const install = electronMock.handlers.get('apps:install')!;
      const setEnabled = electronMock.handlers.get('apps:set-enabled')!;
      const installing = Promise.resolve(install({}, { appId: 'demo-app' }));
      const disabling = Promise.resolve(setEnabled({}, { appId: 'demo-app', enabled: false }));
      await vi.waitFor(() => expect(installStarted).toBe(true));
      let disableSettled = false;
      void disabling.then(() => { disableSettled = true; });
      await Promise.resolve();
      expect(disableSettled).toBe(false);

      installGate.resolve();
      await expect(installing).resolves.toMatchObject({ id: 'demo-app' });
      await expect(disabling).resolves.toMatchObject({ enabled: false });
    } finally {
      installSpy.mockRestore();
    }
  });
});

function deferred<T>(): { promise: Promise<T>; resolve(value: T): void } {
  let resolve!: (value: T) => void;
  return { promise: new Promise<T>((accept) => { resolve = accept; }), resolve };
}

async function createRegisteredApp(enabled: boolean): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), 'workbench-launch-ipc-'));
  roots.push(root);
  const installPath = join(root, 'Workbench', 'apps', 'demo-app', '1.0.0');
  await mkdir(installPath, { recursive: true });
  await writeFile(join(installPath, 'manifest.json'), JSON.stringify({
    schemaVersion: 1,
    id: 'demo-app',
    name: '演示应用',
    description: '测试应用',
    publisherId: 'test',
    version: '1.0.0',
    hostApiVersion: '1.0',
    minWorkbenchVersion: '0.1.0',
    runtime: { kind: 'web', rendererEntry: 'renderer/index.html', icon: 'renderer/icon.png' },
    capabilities: []
  }), 'utf8');
  const repository = new AppRegistryRepository(join(root, 'Workbench', 'apps.db'));
  repository.upsert({
    id: 'demo-app',
    name: '演示应用',
    description: '测试应用',
    publisherId: 'test',
    installedVersion: '1.0.0',
    activeVersion: '1.0.0',
    installPath,
    enabled,
    state: 'installed'
  });
  repository.close();
  return root;
}

function createDevelopmentOverride(root: string): DevelopmentAppOverride {
  return {
    appId: 'demo-app',
    installPath: join(root, 'Workbench', 'apps', 'demo-app', '1.0.0'),
    manifest: {
      schemaVersion: 1,
      id: 'demo-app',
      name: '演示应用',
      description: '测试应用',
      publisherId: 'test',
      version: '1.0.0',
      hostApiVersion: '1.0',
      minWorkbenchVersion: '0.1.0',
      runtime: { kind: 'web', rendererEntry: 'renderer/index.html', icon: 'renderer/icon.png' },
      capabilities: []
    }
  };
}

function createRendererWindow() {
  const renderer = {
    webContents: { send: vi.fn(), isDestroyed: vi.fn(() => false) },
    on: vi.fn().mockReturnThis(),
    once: vi.fn().mockReturnThis(),
    off: vi.fn().mockReturnThis(),
    isMaximized: vi.fn(() => false)
  };
  electronMock.BrowserWindow.getAllWindows.mockReturnValue([renderer]);
  return renderer;
}
