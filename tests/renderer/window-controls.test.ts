import { describe, expect, it } from 'vitest';

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
const styles = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

describe('窗口控制按钮', () => {
  it('主工作台和虚拟窗口都复用独立共享控制组件', () => {
    expect(appSource).toContain("import { WindowControls }");
    expect(appSource).toContain('variant="shell"');
    expect(appSource).toContain('variant="window"');
  });

  it('主工作台从原生状态查询和订阅同步最大化状态', () => {
    expect(appSource).toContain('const [shellMaximized, setShellMaximized] = useState<boolean | undefined>(undefined);');
    expect(appSource).toContain('maximized={shellMaximized}');
    expect(appSource).toContain('window.workbench.shell.isMaximized()');
    expect(appSource).toContain('window.workbench.shell.onMaximizedChanged(setShellMaximized)');
    expect(appSource).toContain("useWindowMaximizeAnimation(desktopShellRef, shellMaximized, 'native');");
    expect(appSource).not.toContain('setShellMaximized((value) => !value)');
  });

  it('样式通过共享令牌统一两套控件的尺寸和中性状态', () => {
    expect(styles).toContain('--window-control-color:');
    expect(styles).toContain('--window-control-hover-background:');
    expect(styles).toContain('--window-control-active-background:');
    expect(styles).toContain('--window-control-transition:');
    expect(styles).toContain('.window-control-button-shell, .window-control-button-window { --window-control-size: 46px; }');
    expect(styles).toContain('.window-titlebar { min-height: 46px;');
    expect(styles).toContain('.window-control-button:hover');
    expect(styles).toContain('.window-control-button:active');
    expect(styles).not.toContain('--window-control-close-background:');
    expect(styles).not.toContain('.window-control-button-close:hover');
  });

  it('Workbench 标题栏不再包含分析中心设置命令或 iframe 引用', () => {
    expect(appSource).not.toContain('打开分析中心设置');
    expect(appSource).not.toContain("command: 'settings.open'");
    expect(appSource).not.toContain('analysisFrameRef');
  });
});
