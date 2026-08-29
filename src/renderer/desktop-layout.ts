import type { DesktopIconLayout } from '../main/data/desktop-layout-repository';

export type DesktopIconPoint = Pick<DesktopIconLayout, 'x' | 'y'>;
export type DesktopAppId = DesktopIconLayout['appId'];

/**
 * 桌面应用图标使用独立于背景装饰线的槽位网格，保证拖动和持久化使用同一套坐标。
 * 坐标相对于 .desktop-icons 容器，起点沿用当前桌面默认布局。
 */
export const DESKTOP_GRID = {
  originX: 44,
  originY: 96,
  cellWidth: 96,
  cellHeight: 118
} as const;

/** 图标按钮的命中区域与样式尺寸保持一致，用于判断拖放目标的前半区和后半区。 */
export const DESKTOP_ICON = { width: 86, height: 102 } as const;

export const DEFAULT_ICON_LAYOUT: Record<DesktopAppId, DesktopIconPoint> = {
  'analysis-center': { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY },
  'app-center': { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY + DESKTOP_GRID.cellHeight },
  terminal: { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY + DESKTOP_GRID.cellHeight * 2 }
};

/**
 * 应用中心始终保留桌面入口，其余入口由已安装应用注册表决定。
 * 已知应用沿用固定顺序，新增应用按标识符排序，确保每次启动的布局稳定。
 */
export function getDesktopAppIds(installedAppIds: readonly DesktopAppId[]): DesktopAppId[] {
  const appIds = new Set<DesktopAppId>(['app-center', ...installedAppIds]);
  return [...appIds].sort((left, right) => desktopAppOrder(left) - desktopAppOrder(right) || left.localeCompare(right));
}

/** 根据当前已安装应用生成默认槽位；新应用始终追加到首列下方的首个空槽位。 */
export function getDefaultIconLayout(installedAppIds: readonly DesktopAppId[]): Record<DesktopAppId, DesktopIconPoint> {
  const layout: Record<DesktopAppId, DesktopIconPoint> = {};
  const occupiedPoints: DesktopIconPoint[] = [];

  for (const appId of getDesktopAppIds(installedAppIds)) {
    const preferredPoint = DEFAULT_ICON_LAYOUT[appId] ?? {
      x: DESKTOP_GRID.originX,
      y: DESKTOP_GRID.originY + occupiedPoints.length * DESKTOP_GRID.cellHeight
    };
    const point = resolveDesktopIconPoint(preferredPoint, occupiedPoints);
    layout[appId] = point;
    occupiedPoints.push(point);
  }

  return layout;
}

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

/**
 * 在已占用的图标槽位中插入拖动目标，而非为它另找空位。
 * 排序先固定当前所有视觉槽位，再仅重映射图标标识，因此用户手动摆放到其他列的坐标不会被压缩或丢失。
 */
export function reorderDesktopIconLayout(
  layout: readonly DesktopIconLayout[],
  movingAppId: string,
  targetAppId: string,
  placement: 'before' | 'after'
): DesktopIconLayout[] {
  if (movingAppId === targetAppId) return [...layout];
  const slots = [...layout].sort(compareDesktopLayout);
  const moving = slots.find((item) => item.appId === movingAppId);
  if (!moving) return [...layout];
  const remaining = slots.filter((item) => item.appId !== movingAppId);
  const targetIndex = remaining.findIndex((item) => item.appId === targetAppId);
  if (targetIndex < 0) return [...layout];
  const insertionIndex = targetIndex + (placement === 'after' ? 1 : 0);
  const ordered = [...remaining.slice(0, insertionIndex), moving, ...remaining.slice(insertionIndex)];
  return ordered.map((item, index) => ({ appId: item.appId, x: slots[index]!.x, y: slots[index]!.y }));
}

/**
 * 根据释放指针命中目标图标的上半区或下半区决定插入位置。
 * 未命中图标时返回 undefined，由调用方保留自由拖动后的吸附坐标。
 */
export function resolveDesktopIconDropLayout(
  layout: readonly DesktopIconLayout[],
  movingAppId: string,
  pointer: DesktopIconPoint
): DesktopIconLayout[] | undefined {
  const target = layout.find((item) => item.appId !== movingAppId
    && pointer.x >= item.x && pointer.x <= item.x + DESKTOP_ICON.width
    && pointer.y >= item.y && pointer.y <= item.y + DESKTOP_ICON.height);
  if (!target) return undefined;
  return reorderDesktopIconLayout(layout, movingAppId, target.appId, pointer.y < target.y + DESKTOP_ICON.height / 2 ? 'before' : 'after');
}

/** 过滤已移除的历史图标、归一化旧版本坐标，并为当前图标分配唯一网格槽位。 */
export function normalizeDesktopLayout(
  layout: readonly DesktopIconLayout[],
  installedAppIds: readonly DesktopAppId[] = Object.keys(DEFAULT_ICON_LAYOUT)
): DesktopIconLayout[] {
  const occupiedPoints: DesktopIconPoint[] = [];
  const defaults = getDefaultIconLayout(installedAppIds);

  return getDesktopAppIds(installedAppIds).map((appId) => {
    const savedPoint = layout.find((item) => item.appId === appId);
    const point = resolveDesktopIconPoint(savedPoint ?? defaults[appId], occupiedPoints);
    occupiedPoints.push(point);
    return { appId, ...point };
  });
}

function desktopAppOrder(appId: DesktopAppId): number {
  if (appId === 'analysis-center') return 0;
  if (appId === 'app-center') return 1;
  if (appId === 'terminal') return 2;
  return 3;
}

function compareDesktopLayout(left: DesktopIconLayout, right: DesktopIconLayout): number {
  return left.y - right.y || left.x - right.x || left.appId.localeCompare(right.appId);
}
