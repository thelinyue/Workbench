import { access } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const workspaceRoot = resolve(__dirname, '../..');

describe('工作台品牌资源', () => {
  it('提供工作台壁纸和统一应用图标源文件', async () => {
    await expect(access(resolve(workspaceRoot, 'src/renderer/src/assets/workbench-wallpaper.png'))).resolves.toBeUndefined();
    await expect(access(resolve(workspaceRoot, 'src/renderer/src/assets/workbench-icon.png'))).resolves.toBeUndefined();
  });

  it('为应用中心和分析中心提供独立 SVG 图标并在渲染层分别引用', async () => {
    await expect(access(resolve(workspaceRoot, 'src/renderer/src/assets/app-center-icon.svg'))).resolves.toBeUndefined();
    await expect(access(resolve(workspaceRoot, 'src/renderer/src/assets/analysis-center-icon.svg'))).resolves.toBeUndefined();

    const appSource = readFileSync(resolve(workspaceRoot, 'src/renderer/src/App.tsx'), 'utf8');
    expect(appSource).toContain("'./assets/app-center-icon.svg'");
    expect(appSource).toContain("'./assets/analysis-center-icon.svg'");
    expect(appSource).toContain('APP_ICON_URLS[id]');
  });
});
