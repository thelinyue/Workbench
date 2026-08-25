import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const styles = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

describe('亮色小清新主题', () => {
  it('定义薄荷蓝工具感的语义化主题令牌', () => {
    expect(styles).toContain('--page-background: #F4FAF8;');
    expect(styles).toContain('--text: #16324F;');
    expect(styles).toContain('--muted: #5F7284;');
    expect(styles).toContain('--surface: #FFFFFF;');
    expect(styles).toContain('--surface-soft: #F0F7F5;');
    expect(styles).toContain('--primary: #0F766E;');
    expect(styles).toContain('--blue: #256B86;');
    expect(styles).toContain('--danger: #C53D55;');
    expect(styles).toContain('--warning: #8A5A00;');
    expect(styles).toContain('--success: #2B8A63;');
    expect(styles).toContain('--line: #D7E8E3;');
  });

  it('让桌面外壳、应用窗口和分析探索区使用浅色层级', () => {
    expect(styles).toContain('body { overflow: hidden; background: var(--page-background); }');
    expect(styles).toContain('background: var(--page-background);');
    expect(styles).toContain('background: var(--surface);');
    expect(styles).toContain('.analysis-explorer { background: var(--surface-soft) !important;');
    expect(styles).not.toContain('.analysis-sidebar');
  });

  it('为状态标签提供浅色背景和可读的语义文字颜色', () => {
    expect(styles).toContain('.status-neutral { color: #526B78; background: #EEF4F3; }');
    expect(styles).toContain('.status-info { color: #256B86; background: #EAF7FB; }');
    expect(styles).toContain('.status-success { color: #2B8A63; background: #EAF7F0; }');
    expect(styles).toContain('.status-danger { color: #C53D55; background: #FFF2F4; }');
  });

  it('只为工作台顶栏和应用窗口标题栏增加透明毛玻璃效果', () => {
    expect(styles).toContain('background-image: var(--workbench-wallpaper);');
    expect(styles).toContain('.topbar {');
    expect(styles).toContain('background: rgba(255, 255, 255, .30);');
    expect(styles).toContain('.window-titlebar {');
    expect(styles).toContain('background: rgba(255, 255, 255, .26);');
    expect(styles).toContain('backdrop-filter: blur(18px) saturate(120%);');
    expect(styles).toContain('@media (prefers-reduced-transparency: reduce)');
  });
});
