import type { DesktopIconLayout } from '../main/data/workspace-repository';

export type DesktopIconPoint = Pick<DesktopIconLayout, 'x' | 'y'>;
export type DesktopAppId = 'analysis-center' | 'app-center';

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
  'app-center': { x: DESKTOP_GRID.originX + DESKTOP_GRID.cellWidth, y: DESKTOP_GRID.originY }
};

/** 将任意拖动坐标吸附到最近的桌面图标槽位，并限制在网格起点之后。 */
export function snapDesktopIconPoint(point: DesktopIconPoint): DesktopIconPoint {
  return {
    x: DESKTOP_GRID.originX + Math.max(0, Math.round((point.x - DESKTOP_GRID.originX) / DESKTOP_GRID.cellWidth)) * DESKTOP_GRID.cellWidth,
    y: DESKTOP_GRID.originY + Math.max(0, Math.round((point.y - DESKTOP_GRID.originY) / DESKTOP_GRID.cellHeight)) * DESKTOP_GRID.cellHeight
  };
}

function desktopIconSlotKey(point: DesktopIconPoint): string {
  return `${point.x}:${point.y}`;
}

/**
 * 将图标放入最近的空网格槽位。
 *
 * 先按统一网格吸附目标坐标；如果目标槽位已被占用，则按曼哈顿距离逐圈搜索，
 * 并在同一距离内按照从上到下、从左到右的顺序选择，保证多个图标不会重叠且结果稳定。
 */
export function resolveDesktopIconPoint(point: DesktopIconPoint, occupiedPoints: readonly DesktopIconPoint[]): DesktopIconPoint {
  const snapped = snapDesktopIconPoint(point);
  const occupied = new Set(occupiedPoints.map((item) => desktopIconSlotKey(snapDesktopIconPoint(item))));
  if (!occupied.has(desktopIconSlotKey(snapped))) return snapped;

  const centerColumn = Math.round((snapped.x - DESKTOP_GRID.originX) / DESKTOP_GRID.cellWidth);
  const centerRow = Math.round((snapped.y - DESKTOP_GRID.originY) / DESKTOP_GRID.cellHeight);

  for (let radius = 1; ; radius += 1) {
    const candidates: DesktopIconPoint[] = [];
    const minColumn = Math.max(0, centerColumn - radius);
    const minRow = Math.max(0, centerRow - radius);
    const maxColumn = centerColumn + radius;
    const maxRow = centerRow + radius;

    for (let row = minRow; row <= maxRow; row += 1) {
      for (let column = minColumn; column <= maxColumn; column += 1) {
        if (Math.abs(column - centerColumn) + Math.abs(row - centerRow) !== radius) continue;
        candidates.push({
          x: DESKTOP_GRID.originX + column * DESKTOP_GRID.cellWidth,
          y: DESKTOP_GRID.originY + row * DESKTOP_GRID.cellHeight
        });
      }
    }

    candidates.sort((left, right) => left.y - right.y || left.x - right.x);
    const available = candidates.find((candidate) => !occupied.has(desktopIconSlotKey(candidate)));
    if (available) return available;
  }
}

/** 过滤已移除的历史图标、归一化旧版本坐标，并为当前图标分配唯一网格槽位。 */
export function normalizeDesktopLayout(layout: readonly DesktopIconLayout[]): DesktopIconLayout[] {
  const occupiedPoints: DesktopIconPoint[] = [];

  return (Object.keys(DEFAULT_ICON_LAYOUT) as DesktopAppId[]).map((appId) => {
    const savedPoint = layout.find((item) => item.appId === appId);
    const point = resolveDesktopIconPoint(savedPoint ?? DEFAULT_ICON_LAYOUT[appId], occupiedPoints);
    occupiedPoints.push(point);
    return { appId, ...point };
  });
}
