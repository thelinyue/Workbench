import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('分析中心快捷菜单', () => {
  it('通过菜单外指针按下关闭，避免打开菜单的点击事件立即把它关闭', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain("window.addEventListener('pointerdown', closeMenu)");
    expect(appSource).not.toContain("window.addEventListener('click', closeMenu)");
    expect(appSource).toContain('onPointerDown={(event) => event.stopPropagation()}');
  });

  it('将菜单渲染到文档层，避免被应用窗口的 overflow 裁剪', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain("import { createPortal } from 'react-dom'");
    expect(appSource).toContain('return createPortal(');
  });
});
