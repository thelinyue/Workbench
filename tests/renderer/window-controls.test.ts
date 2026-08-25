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

  it('共享控制组件使用最小化、最大化和关闭图标', () => {
    expect(appSource).toContain('<Minimize2 size={16} strokeWidth={1.8} />');
    expect(appSource).toContain('<Maximize2 size={16} strokeWidth={1.8} />');
    expect(appSource).toContain('<X size={16} strokeWidth={1.8} />');
  });

  it('样式通过语义令牌统一普通态、交互态和关闭态', () => {
    expect(styles).toContain('--window-control-color:');
    expect(styles).toContain('--window-control-hover-background:');
    expect(styles).toContain('--window-control-active-background:');
    expect(styles).toContain('--window-control-close-background:');
    expect(styles).toContain('--window-control-transition:');
    expect(styles).toContain('.window-control-button:hover');
    expect(styles).toContain('.window-control-button:active');
    expect(styles).toContain('.window-control-button-close:hover');
  });
});
