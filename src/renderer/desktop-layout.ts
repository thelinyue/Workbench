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

/** 根据当前已安装应用生成默认槽位；所有应用始终从首列顶部连续向下排列。 */
export function getDefaultIconLayout(installedAppIds: readonly DesktopAppId[]): Record<DesktopAppId, DesktopIconPoint> {
  return Object.fromEntries(getDesktopAppIds(installedAppIds).map((appId, index) => [appId, verticalSlot(index)])) as Record<DesktopAppId, DesktopIconPoint>;
}

/**
 * 在已占用的图标槽位中插入拖动目标，而非为它另找空位。
 * 排序完成后重新映射到连续竖向槽位，确保桌面不再保留自由坐标。
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
  return ordered.map((item, index) => ({ appId: item.appId, ...verticalSlot(index) }));
}

/**
 * 根据释放指针命中目标图标的上半区或下半区决定插入位置。
 * 未命中图标时返回 undefined，由调用方保持当前顺序和竖向位置。
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

/**
 * 过滤已移除应用并把任意历史坐标压缩为首列竖向顺序。
 * 已保存应用按当前视觉顺序排列，缺失的新应用继续按默认顺序追加到底部。
 */
export function normalizeDesktopLayout(
  layout: readonly DesktopIconLayout[],
  installedAppIds: readonly DesktopAppId[] = Object.keys(DEFAULT_ICON_LAYOUT)
): DesktopIconLayout[] {
  const appIds = getDesktopAppIds(installedAppIds);
  const availableAppIds = new Set(appIds);
  const seen = new Set<string>();
  const savedOrder = [...layout]
    .filter((item) => availableAppIds.has(item.appId))
    .sort(compareDesktopLayout)
    .filter((item) => !seen.has(item.appId) && Boolean(seen.add(item.appId)))
    .map((item) => item.appId);
  const orderedAppIds = [...savedOrder, ...appIds.filter((appId) => !seen.has(appId))];
  return orderedAppIds.map((appId, index) => ({ appId, ...verticalSlot(index) }));
}

function verticalSlot(index: number): DesktopIconPoint {
  return { x: DESKTOP_GRID.originX, y: DESKTOP_GRID.originY + index * DESKTOP_GRID.cellHeight };
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
