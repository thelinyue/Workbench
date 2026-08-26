import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('桌面顶栏', () => {
  it('不渲染系统在线状态指示器', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).not.toContain('系统在线');
    expect(appSource).not.toContain('health-indicator');
    expect(appSource).not.toContain('health-dot');
  });

  it('返回桌面按钮使用原始 Monitor 图标而不是工作台品牌图标', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain('<Monitor size={17} />');
    expect(appSource).not.toContain('aria-label="返回桌面" onClick={() => { setWindows((current) => current.map((item) => ({ ...item, minimized: true }))); setDrawerOpen(false); }}><img className="shell-brand-icon" src={WORKBENCH_ICON_URL}');
  });
});
