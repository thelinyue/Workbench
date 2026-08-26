import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ICON_LAYOUT,
  DESKTOP_GRID,
  normalizeDesktopLayout,
  resolveDesktopIconPoint,
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

  it('为内置 SSH 终端保留分析中心之后的固定默认槽位', () => {
    expect(DEFAULT_ICON_LAYOUT.terminal).toEqual({ x: 276, y: 96 });
    expect(normalizeDesktopLayout([], ['analysis-center', 'app-center', 'terminal'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'terminal', x: 276, y: 96 }
    ]);
  });

  it('目标槽位被占用时吸附到最近的空槽位', () => {
    expect(resolveDesktopIconPoint(DEFAULT_ICON_LAYOUT['analysis-center'], [DEFAULT_ICON_LAYOUT['analysis-center']])).toEqual({ x: 160, y: 96 });
  });

  it('最近空槽位距离相同时按从上到下、从左到右稳定选择', () => {
    const target = { x: 44 + DESKTOP_GRID.cellWidth, y: 96 + DESKTOP_GRID.cellHeight };
    const occupied = [target, { x: 44 + DESKTOP_GRID.cellWidth, y: 96 }];

    expect(resolveDesktopIconPoint(target, occupied)).toEqual({ x: 44, y: 238 });
  });

  it('启动归一化历史布局并补充两个内置核心应用入口', () => {
    expect(normalizeDesktopLayout([{ appId: 'analysis-center', x: 198, y: 390 }])).toEqual([
      { appId: 'analysis-center', x: 160, y: 380 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'terminal', x: 276, y: 96 }
    ]);
  });

  it('启动时过滤历史设置图标记录', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 238 },
      { appId: 'settings', x: 44, y: 238 }
    ])).toEqual([
      { appId: 'analysis-center', x: 44, y: 238 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'terminal', x: 276, y: 96 }
    ]);
  });

  it('启动时修复分析中心的历史重复槽位', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 238 },
      { appId: 'analysis-center', x: 44, y: 238 }
    ])).toEqual([
      { appId: 'analysis-center', x: 44, y: 238 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'terminal', x: 276, y: 96 }
    ]);
  });

  it('应用注册表包含已安装 LVM 工具时才为它分配桌面槽位', () => {
    expect(normalizeDesktopLayout([], ['analysis-center', 'app-center', 'lvm-uncache-tool'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'lvm-uncache-tool', x: 276, y: 96 }
    ]);
  });

  it('未安装应用不会从历史布局恢复到桌面', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 160, y: 96 },
      { appId: 'lvm-uncache-tool', x: 276, y: 96 }
    ], ['analysis-center', 'app-center'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 160, y: 96 }
    ]);
  });

  it('公开当前桌面网格的固定参数，避免拖拽和持久化使用不同网格', () => {
    expect(DESKTOP_GRID).toEqual({ originX: 44, originY: 96, cellWidth: 116, cellHeight: 142 });
  });
});
