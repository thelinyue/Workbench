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
  return [...windows, { id, title, x: 140, y: 84, width: 980, height: 650, zIndex: nextZIndex, minimized: false, maximized: false }];
}

export function moveWindow(windows: AppWindow[], id: string, x: number, y: number): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, x, y } : item);
}

export function minimizeWindow(windows: AppWindow[], id: string): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, minimized: !item.minimized } : item);
}

/** 限制窗口最小尺寸，避免内容区在拖拽缩放时不可操作。 */
export function resizeWindow(windows: AppWindow[], id: string, width: number, height: number): AppWindow[] {
  return windows.map((item) => item.id === id ? { ...item, width: Math.max(640, width), height: Math.max(420, height) } : item);
}
