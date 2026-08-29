import { describe, expect, it } from 'vitest';
import { createMainWindowOptions } from '../../src/main/main-window-options';

describe('工作台主窗口默认配置', () => {
  it('首次打开时使用截图对应的 1024 x 680 尺寸', () => {
    expect(createMainWindowOptions()).toMatchObject({
      width: 1024,
      height: 680,
      minWidth: 1024,
      minHeight: 680,
      center: true,
      frame: false
    });
  });
});
