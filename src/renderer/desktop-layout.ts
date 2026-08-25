import type { DesktopIconLayout } from '../main/data/workspace-repository';

export type DesktopIconPoint = Pick<DesktopIconLayout, 'x' | 'y'>;
export type DesktopAppId = DesktopIconLayout['appId'];

/**
 * 桌面应用图标使用独立于背景装饰线的槽位网格，保证拖动和持久化使用同一套坐标。
 * 坐标相对于 .desktop-icons 容器，起点沿用当前桌面默认布局。
 */
export const DESKTOP_GRID = {
  originX: 44,
  originY: 96,
  cellWidth: 116,
  cellHeight: 142
} as const;

export const DEFAULT_ICON_LAYOUT: Record<DesktopAppId, DesktopIconPoint> = {
  'analysis-center': { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY },
  settings: { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY + DESKTOP_GRID.cellHeight }
};

/** 将任意拖动坐标吸附到最近的桌面图标槽位，并限制在网格起点之后。 */
export function snapDesktopIconPoint(point: DesktopIconPoint): DesktopIconPoint {
  return {
    x: DESKTOP_GRID.originX + Math.max(0, Math.round((point.x - DESKTOP_GRID.originX) / DESKTOP_GRID.cellWidth)) * DESKTOP_GRID.cellWidth,
    y: DESKTOP_GRID.originY + Math.max(0, Math.round((point.y - DESKTOP_GRID.originY) / DESKTOP_GRID.cellHeight)) * DESKTOP_GRID.cellHeight
  };
}

/** 补齐新增图标并归一化旧版本保存的非网格坐标。 */
export function normalizeDesktopLayout(layout: readonly DesktopIconLayout[]): DesktopIconLayout[] {
  const next: Record<DesktopAppId, DesktopIconPoint> = {
    'analysis-center': { ...DEFAULT_ICON_LAYOUT['analysis-center'] },
    settings: { ...DEFAULT_ICON_LAYOUT.settings }
  };

  for (const item of layout) next[item.appId] = snapDesktopIconPoint(item);

  return (Object.keys(next) as DesktopAppId[]).map((appId) => ({ appId, ...next[appId] }));
}
