import type { BrowserWindowConstructorOptions } from 'electron';

/**
 * 工作台主窗口固定以紧凑桌面尺寸打开；最小尺寸与初始尺寸一致，
 * 避免缩小时出现无法使用的桌面布局。
 */
export function createMainWindowOptions(): BrowserWindowConstructorOptions {
  return {
    width: 1024,
    height: 680,
    minWidth: 1024,
    minHeight: 680,
    center: true,
    show: false,
    frame: false
  };
}
