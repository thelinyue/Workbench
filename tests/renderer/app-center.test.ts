import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';

const source = await readFile(new URL('../../src/renderer/src/App.tsx', import.meta.url), 'utf8');
const styles = await readFile(new URL('../../src/renderer/src/styles.css', import.meta.url), 'utf8');

describe('应用中心界面', () => {
  it('提供应用中心窗口并从应用 API 加载目录状态', () => {
    expect(source).toContain('function AppCenter');
    expect(source).toContain('window.workbench.apps.list()');
    expect(source).toContain('window.workbench.apps.refreshCatalog()');
    expect(source).toContain('window.workbench.apps.install(');
    expect(source).toContain('应用中心');
  });

  it('将应用名称留在窗口栏，并保留目录刷新操作', () => {
    expect(source).not.toContain('<h1>应用中心</h1>');
    expect(source).toContain('刷新目录');
    expect(source).toContain('refreshCatalog()');
    expect(styles).toContain('.app-center-toolbar { display: flex; justify-content: flex-end; margin-bottom: 12px; }');
    expect(styles).toContain('.app-card-grid { display: grid; margin-top: 0;');
  });

  it('应用窗口由 registry 中的应用 ID 打开，而不是写死分析中心组件', () => {
    expect(source).toContain("item.id === 'app-center'");
    expect(source).toContain('item.id !== \'app-center\'');
    expect(source).toContain('APP_META');
    expect(source).toContain('onOpenApp(item.id)');
  });

  it('将 SSH 终端作为内置核心应用提供固定元数据和启动入口', () => {
    expect(source).toContain("'terminal': { title: 'SSH 终端'");
    expect(source).toContain("terminal: new URL('./assets/terminal-icon.svg'");
    expect(source).toContain("item.id !== 'app-center' && <EmbeddedApp");
  });

  it('所有已安装应用统一通过 launch 结果选择原生或内嵌展示', () => {
    expect(source).toContain('await launchDesktopApp(id, window.workbench.apps, () => showVirtualAppWindow(id))');
    expect(source).not.toContain("if ((id === 'analysis-center' || id === 'terminal')");
  });

  it('分析任务刷新失败时仍在运行时启动后打开应用窗口', () => {
    expect(source).toMatch(/await launchDesktopApp\(id,[\s\S]*?if \(id === 'analysis-center'\) void Promise\.all/);
  });

  it('应用中心只调用统一 openApp，不重复启动 runtime', () => {
    expect(source).toMatch(/const launch = async \(item: AppInstallRecord\)[\s\S]*?await onOpenApp\(item\.id\);/);
  });

  it('应用中心卡片使用应用自己的图标，应用库只显示应用中心和已安装应用', () => {
    expect(source).toContain('<img src={resolveAppIconUrl(item.id)}');
    expect(source).not.toContain('<img src={WORKBENCH_ICON_URL} alt="" aria-hidden="true" /></div><div className="app-card-body">');
    expect(source).toContain("filter((id) => id === 'app-center'");
    expect(source).toContain("registeredApps.some((item) => item.id === id && item.activeVersion)");
  });

  it('应用中心页面和卡片使用冷灰蓝亮色表面', () => {
    expect(styles).toMatch(/\.app-center-view\s*\{[^}]*background:\s*var\(--page-background\);/);
    expect(styles).toMatch(/\.app-card\s*\{[^}]*background:\s*var\(--surface\);/);
    expect(styles).toContain('.app-card-title h2 { color: var(--text);');
    expect(styles).toContain("html[data-theme='light'] .app-state-installed { color: var(--success); background: #EAF7F0; }");
    expect(styles).toContain("html[data-theme='light'] .app-state-incompatible,");
    expect(styles).toContain("html[data-theme='light'] .app-state-broken { color: var(--danger); background: #FFF2F4; }");
    expect(styles).toContain('.primary-button { border-color: var(--primary); color: #FFFFFF; background: var(--primary); }');
  });
});
