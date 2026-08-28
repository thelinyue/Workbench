import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';

const source = await readFile(new URL('../../src/main/index.ts', import.meta.url), 'utf8');

describe('开发应用启动装配', () => {
  it('仅在 Electron 开发运行时加载覆盖，并将同一记录交给协议和 IPC', () => {
    expect(source).toContain("loadDevelopmentAppOverride({ isPackaged: app.isPackaged })");
    expect(source).toContain("registerAppResourceProtocol(join(app.getPath('userData'), 'Workbench', 'apps'), developmentOverride, () => developmentOverrideEnabled)");
    expect(source).toContain("registerWorkbenchIpc(app.getPath('userData'), {");
    expect(source).toContain('onDevelopmentOverrideStateChange');
    expect(source).toContain('resolveAppWindow: (webContentsId) => appWindowManager.resolveWebContents(webContentsId)');
    expect(source).toContain('installHostNavigationGuard(window.webContents');
  });

  it('最终清理在 runtime drain 后销毁主窗口，再关闭协议和状态仓储', () => {
    const runtime = source.indexOf("{ name: 'Workbench IPC 与应用运行时'");
    const mainWindow = source.indexOf("{ name: 'Workbench 主窗口'");
    const protocol = source.indexOf("{ name: '应用资源协议'");
    const repository = source.indexOf("{ name: '应用窗口状态仓储'");

    expect(mainWindow).toBeGreaterThan(runtime);
    expect(mainWindow).toBeLessThan(protocol);
    expect(mainWindow).toBeLessThan(repository);
  });
});
