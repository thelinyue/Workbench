import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ICON_LAYOUT,
  DESKTOP_GRID,
  normalizeDesktopLayout,
  snapDesktopIconPoint
} from '../../src/renderer/desktop-layout';

describe('桌面应用图标网格布局', () => {
  it('将拖动中的图标吸附到最近网格点', () => {
    expect(snapDesktopIconPoint({ x: 44 + 116 * 0.49, y: 96 + 142 * 0.51 })).toEqual({ x: 44, y: 238 });
    expect(snapDesktopIconPoint({ x: 44 + 116 * 1.51, y: 96 + 142 * 1.49 })).toEqual({ x: 276, y: 238 });
  });

  it('不会把图标吸附到桌面网格起点之前', () => {
    expect(snapDesktopIconPoint({ x: 0, y: 0 })).toEqual(DEFAULT_ICON_LAYOUT['analysis-center']);
  });

  it('启动归一化历史布局并补齐缺失的应用图标', () => {
    expect(normalizeDesktopLayout([{ appId: 'analysis-center', x: 198, y: 390 }])).toEqual([
      { appId: 'analysis-center', x: 160, y: 380 },
      { appId: 'settings', ...DEFAULT_ICON_LAYOUT.settings }
    ]);
  });

  it('公开当前桌面网格的固定参数，避免拖拽和持久化使用不同网格', () => {
    expect(DESKTOP_GRID).toEqual({ originX: 44, originY: 96, cellWidth: 116, cellHeight: 142 });
  });
});
