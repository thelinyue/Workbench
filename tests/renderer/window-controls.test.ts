import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
const styles = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

describe('窗口控制按钮', () => {
  it('主工作台和虚拟窗口都复用共享控制组件', () => {
    expect(appSource).toContain('function WindowControls(');
    expect(appSource.match(/<WindowControls\b/g)?.length).toBe(2);
    expect(appSource).toContain('variant="shell"');
    expect(appSource).toContain('variant="window"');
    expect(appSource).toContain('maximizeAriaLabel="最大化或还原工作台"');
  });

  it('主工作台根据最大化状态切换方框和还原图标', () => {
    expect(appSource).toContain('const [shellMaximized, setShellMaximized] = useState(false);');
    expect(appSource).toContain('maximized={shellMaximized}');
    expect(appSource).toContain('setShellMaximized((value) => !value)');
  });

  it('共享控制组件使用 Windows 风格图标并在最大化时切换还原图标', () => {
    expect(appSource).toContain('<Minus size={14} strokeWidth={1.5} />');
    expect(appSource).toContain('{maximized ? <Copy size={14} strokeWidth={1.5} /> : <Square size={14} strokeWidth={1.5} />}');
    expect(appSource).toContain('<X size={14} strokeWidth={1.5} />');
    expect(appSource).not.toContain('Minimize2');
    expect(appSource).not.toContain('Maximize2');
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

  it('只在分析中心标题栏中将设置齿轮放在最小化按钮左侧', () => {
    expect(appSource).toContain("item.id === 'analysis-center'");
    expect(appSource).toContain('aria-label="打开分析中心设置"');
    expect(appSource).toContain('<Settings size={15} strokeWidth={1.5} />');
    expect(appSource).toContain("type: 'workbench-app-command'");
    expect(appSource).toContain("command: 'settings.open'");
  });

  it('保持分析中心 iframe 引用回调稳定，避免壳层重渲染时重新加载应用', () => {
    expect(appSource).toContain('const handleAnalysisFrameWindowChange = useCallback((frameWindow: Window | null) => {');
    expect(appSource).toContain('onFrameWindowChange={handleAnalysisFrameWindowChange}');
  });
});
