import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';
import { beginAppOperation, completeAppOperation, isAppOperationBusy, type AppOperationState } from '../../src/renderer/app-operation-state';

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

  it('应用中心卡片使用应用自己的图标，应用库只显示应用中心和已启用应用', () => {
    expect(source).toContain('<img src={resolveAppIconUrl(item.id)}');
    expect(source).not.toContain('<img src={WORKBENCH_ICON_URL} alt="" aria-hidden="true" /></div><div className="app-card-body">');
    expect(source).toContain("filter((id) => id === 'app-center'");
    expect(source).toContain("registeredApps.some((item) => item.id === id && item.activeVersion && item.enabled)");
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

  it('已安装卡片提供切换状态按钮并显示运行时状态', () => {
    expect(source).toContain('className="app-card-action-button app-status-button"');
    expect(source).toContain('aria-pressed={item.enabled}');
    expect(source).toContain('aria-label={`${item.enabled ? \'停用\' : \'启用\'}${item.name}`}');
    expect(source).toContain('onClick={() => void setEnabled(item, !item.enabled)}');
    expect(source).not.toContain('<input type="checkbox" checked={item.enabled}');
    expect(source).toContain('window.workbench.apps.setEnabled(item.id');
    expect(source).toContain('item.runtimeState');
    expect(source).toContain('aria-busy={busy}');
  });

  it('应用卡片的安装、更新、打开、状态和卸载操作统一尺寸并右侧对齐', () => {
    expect(source).toContain('className="app-card-action-button primary-button"');
    expect(source).toContain('className="app-card-action-button app-uninstall-button"');
    expect(source).toContain('onClick={() => requestUninstall(item)}>卸载</button>');
    expect(source).not.toContain('onClick={() => requestUninstall(item)}><Trash2 size={16} />');
    expect(styles).toContain('.app-card-action-button { width: 112px; min-height: 34px;');
    expect(styles).toContain('.app-status-button { display: inline-flex; min-height: 34px;');
    expect(styles).toContain('.app-card-actions { display: flex; min-width: 112px;');
    expect(styles).toContain('align-items: flex-end;');
  });

  it('停用应用仍显示卡片但打开操作不可用，并提供启用提示路径', () => {
    expect(source).toContain('disabled={!item.enabled || busy}');
    expect(source).toContain('请先在应用中心启用');
  });

  it('非内置应用提供卸载确认，确认默认不删除数据', () => {
    expect(source).toContain('window.workbench.apps.uninstall(');
    expect(source).toContain('builtIn');
    expect(source).toContain('Trash2');
    expect(source).toContain('aria-label={`卸载${item.name}`}');
    expect(source).toContain('配置、历史记录和报告');
    expect(source).toContain('const [deleteData, setDeleteData] = useState(false);');
  });

  it('卸载确认打开后聚焦取消，Escape 仅在非忙碌时关闭并清理监听', () => {
    expect(source).toContain('cancelButtonRef.current?.focus()');
    expect(source).toContain("event.key === 'Escape' && !busy");
    expect(source).toContain('document.removeEventListener');
  });

});

describe('应用中心操作状态', () => {
  it('A 操作未完成时 B 不能覆盖 busy 状态并让 A 提前解锁', () => {
    let state: AppOperationState = { activeAppId: null };
    state = beginAppOperation(state, 'app-a');
    expect(isAppOperationBusy(state)).toBe(true);

    state = beginAppOperation(state, 'app-b');
    expect(state.activeAppId).toBe('app-a');

    state = completeAppOperation(state, 'app-b');
    expect(isAppOperationBusy(state)).toBe(true);
    state = completeAppOperation(state, 'app-a');
    expect(isAppOperationBusy(state)).toBe(false);
  });
});
