/**
 * 应用内虚拟窗口的纯状态操作。
 *
 * 窗口层级只存在于 React 桌面中，不映射为多个 Electron BrowserWindow；这样任务抽屉、
 * 应用总览和焦点管理始终处于同一工作台上下文。
 */
export interface AppWindow {
  id: string;
  title: string;
  x: number;
  y: number;
  width: number;
  height: number;
  zIndex: number;
  minimized: boolean;
  maximized: boolean;
}

export function createAppWindow(windows: AppWindow[], id: string, title: string): AppWindow[] {
  if (windows.some((item) => item.id === id)) return windows;
  const nextZIndex = Math.max(0, ...windows.map((item) => item.zIndex)) + 1;
  // 新打开的应用使用稳定的默认尺寸，避免每次启动时受屏幕分辨率影响；用户仍可手动最大化、拖动和缩放。
  return [...windows, { id, title, x: 120, y: 48, width: 960, height: 640, zIndex: nextZIndex, minimized: false, maximized: false }];
}

export function moveWindow(windows: AppWindow[], id: string, x: number, y: number): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, x, y } : item);
}

export function minimizeWindow(windows: AppWindow[], id: string): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, minimized: !item.minimized } : item);
}

/** 返回需要显示在桌面窗口层的窗口；最小化窗口由顶部应用切换器负责恢复。 */
export function getVisibleWindows(windows: AppWindow[]): AppWindow[] {
  return windows.filter((item) => !item.minimized);
}

/** 限制窗口最小尺寸，避免内容区在拖拽缩放时不可操作。 */
export function resizeWindow(windows: AppWindow[], id: string, width: number, height: number): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, width: Math.max(640, width), height: Math.max(420, height) } : item);
}
