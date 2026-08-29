import { describe, expect, it } from 'vitest';
import {
  DEFAULT_ICON_LAYOUT,
  DESKTOP_GRID,
  getDefaultIconLayout,
  normalizeDesktopLayout,
  resolveDesktopIconDropLayout,
  reorderDesktopIconLayout,
  resolveDesktopIconPoint,
  snapDesktopIconPoint
} from '../../src/renderer/desktop-layout';

describe('桌面应用图标网格布局', () => {
  it('将拖动中的图标吸附到最近网格点', () => {
    expect(snapDesktopIconPoint({ x: 44 + 96 * 0.49, y: 96 + 118 * 0.51 })).toEqual({ x: 44, y: 214 });
    expect(snapDesktopIconPoint({ x: 44 + 96 * 1.51, y: 96 + 118 * 1.49 })).toEqual({ x: 236, y: 214 });
  });

  it('不会把图标吸附到桌面网格起点之前', () => {
    expect(snapDesktopIconPoint({ x: 0, y: 0 })).toEqual(DEFAULT_ICON_LAYOUT['analysis-center']);
  });

  it('为内置 SSH 终端保留分析中心下方的固定默认槽位', () => {
    expect(DEFAULT_ICON_LAYOUT.terminal).toEqual({ x: 44, y: 332 });
    expect(normalizeDesktopLayout([], ['analysis-center', 'app-center', 'terminal'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('为后续安装的应用继续分配首列的竖向默认槽位', () => {
    expect(getDefaultIconLayout(['analysis-center', 'app-center', 'terminal', 'lvm-uncache-tool'])).toEqual({
      'analysis-center': { x: 44, y: 96 },
      'app-center': { x: 44, y: 214 },
      terminal: { x: 44, y: 332 },
      'lvm-uncache-tool': { x: 44, y: 450 }
    });
  });

  it('目标槽位被占用时吸附到最近的空槽位', () => {
    expect(resolveDesktopIconPoint(DEFAULT_ICON_LAYOUT['analysis-center'], [DEFAULT_ICON_LAYOUT['analysis-center']])).toEqual({ x: 140, y: 96 });
  });

  it('最近空槽位距离相同时按从上到下、从左到右稳定选择', () => {
    const target = { x: 44 + DESKTOP_GRID.cellWidth, y: 96 + DESKTOP_GRID.cellHeight };
    const occupied = [target, { x: 44 + DESKTOP_GRID.cellWidth, y: 96 }];

    expect(resolveDesktopIconPoint(target, occupied)).toEqual({ x: 44, y: 214 });
  });

  it('将 B 插入 A 前时使用已有槽位顺延 A 和后续图标', () => {
    expect(reorderDesktopIconLayout([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ], 'app-center', 'analysis-center', 'before')).toEqual([
      { appId: 'app-center', x: 44, y: 96 },
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('将 B 插入 A 后时使用已有槽位顺延中间图标', () => {
    expect(reorderDesktopIconLayout([
      { appId: 'app-center', x: 44, y: 96 },
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ], 'app-center', 'analysis-center', 'after')).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('拖到目标上半区时将图标插入目标前，拖到下半区时插入目标后', () => {
    const layout = [
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ];

    expect(resolveDesktopIconDropLayout(layout, 'app-center', { x: 60, y: 112 })).toEqual([
      { appId: 'app-center', x: 44, y: 96 },
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
    expect(resolveDesktopIconDropLayout(layout, 'app-center', { x: 60, y: 160 })).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('释放在空白区域时不返回排序布局，保留自由移动后的坐标', () => {
    expect(resolveDesktopIconDropLayout([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 }
    ], 'app-center', { x: 260, y: 400 })).toBeUndefined();
  });

  it('启动归一化历史布局并补充两个内置核心应用入口', () => {
    expect(normalizeDesktopLayout([{ appId: 'analysis-center', x: 198, y: 390 }])).toEqual([
      { appId: 'analysis-center', x: 236, y: 332 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('启动时过滤历史设置图标记录', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'settings', x: 44, y: 214 }
    ])).toEqual([
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'app-center', x: 44, y: 96 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('启动时修复分析中心的历史重复槽位', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'analysis-center', x: 44, y: 214 }
    ])).toEqual([
      { appId: 'analysis-center', x: 44, y: 214 },
      { appId: 'app-center', x: 44, y: 96 },
      { appId: 'terminal', x: 44, y: 332 }
    ]);
  });

  it('应用注册表包含已安装 LVM 工具时才为它分配桌面槽位', () => {
    expect(normalizeDesktopLayout([], ['analysis-center', 'app-center', 'lvm-uncache-tool'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'lvm-uncache-tool', x: 44, y: 332 }
    ]);
  });

  it('未安装应用不会从历史布局恢复到桌面', () => {
    expect(normalizeDesktopLayout([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 },
      { appId: 'lvm-uncache-tool', x: 44, y: 332 }
    ], ['analysis-center', 'app-center'])).toEqual([
      { appId: 'analysis-center', x: 44, y: 96 },
      { appId: 'app-center', x: 44, y: 214 }
    ]);
  });

  it('公开当前桌面网格的固定参数，避免拖拽和持久化使用不同网格', () => {
    expect(DESKTOP_GRID).toEqual({ originX: 44, originY: 96, cellWidth: 96, cellHeight: 118 });
  });
});
