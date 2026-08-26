import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const styles = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');
const index = readFileSync(resolve(process.cwd(), 'src/renderer/index.html'), 'utf8');

describe('冷灰蓝亮色主题', () => {
  it('从文档根节点声明亮色主题并定义 B 方案令牌', () => {
    expect(index).toContain('<html lang="zh-CN" data-theme="light" style="color-scheme: light;">');
    expect(styles).toContain('color-scheme: light;');
    expect(styles).toContain('--page-background: #F4F7FB;');
    expect(styles).toContain('--text: #1E3044;');
    expect(styles).toContain('--muted: #60748A;');
    expect(styles).toContain('--surface: #FFFFFF;');
    expect(styles).toContain('--surface-soft: #EEF3F8;');
    expect(styles).toContain('--primary: #2D6A9F;');
    expect(styles).toContain('--blue: #256B86;');
    expect(styles).toContain('--danger: #C53D55;');
    expect(styles).toContain('--warning: #8A5A00;');
    expect(styles).toContain('--success: #2B8A63;');
    expect(styles).toContain('--line: #DBE4EF;');
    expect(styles).toMatch(/html\[data-theme=['"]light['"]\]/);
  });

  it('让桌面外壳、应用窗口、应用中心和分析探索区使用浅色层级', () => {
    expect(styles).toContain('body { overflow: hidden; background: var(--page-background); }');
    expect(styles).toContain('background: var(--page-background);');
    expect(styles).toContain('background: var(--surface);');
    expect(styles).toContain('.app-center-view {');
    expect(styles).toContain('.analysis-view, .settings-view {');
    expect(styles).toContain("html[data-theme='light'] .analysis-explorer { background: var(--surface-soft) !important;");
    expect(styles).not.toContain('rgba(20, 25, 60, .86)');
    expect(styles).not.toContain('rgba(12, 16, 41, .9)');
    expect(styles).not.toContain('.analysis-sidebar');
  });

  it('为状态标签提供浅色背景和可读的语义文字颜色', () => {
    expect(styles).toContain("html[data-theme='light'] .status-neutral { color: #526B78; background: #EEF3F8; }");
    expect(styles).toContain('.status-info { color: #256B86; background: #EAF7FB; }');
    expect(styles).toContain('.status-success { color: #2B8A63; background: #EAF7F0; }');
    expect(styles).toContain('.status-danger { color: #C53D55; background: #FFF2F4; }');
  });

  it('只为工作台顶栏和应用窗口标题栏增加透明毛玻璃效果', () => {
    expect(styles).toContain('background-image: var(--workbench-wallpaper);');
    expect(styles).toContain('.topbar {');
    expect(styles).toContain('background: var(--surface);');
    expect(styles).toContain('.window-titlebar {');
    expect(styles).toContain('background: var(--surface-soft);');
    expect(styles).toContain('backdrop-filter: blur(18px) saturate(120%);');
    expect(styles).toContain('@media (prefers-reduced-transparency: reduce)');
  });

  it('让桌面壁纸等比例铺满并优先保留右侧内容', () => {
    expect(styles).toContain('background-position: right center;');
    expect(styles).toContain('background-repeat: no-repeat;');
    expect(styles).toContain('background-size: cover;');
  });
});
