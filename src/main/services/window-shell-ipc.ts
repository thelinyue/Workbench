import { app, BrowserWindow, ipcMain } from 'electron';

const SHELL_CHANNELS = [
  'shell:minimize-window',
  'shell:toggle-maximize-window',
  'shell:close-window',
  'shell:is-maximized'
] as const;

/**
 * 注册所有基于 IPC 发送方 BrowserWindow 的原生窗口控制。
 *
 * 最大化状态从 Electron 原生 maximize/unmaximize 事件广播，因此双击标题栏、系统菜单、
 * Windows 快捷键等非按钮路径也会更新自绘控件；任何操作都只作用于发送消息的窗口。
 */
export function registerWindowShellIpc(): () => void {
  const windowCleanups = new Map<BrowserWindow, () => void>();
  const bindWindow = (window: BrowserWindow) => {
    if (windowCleanups.has(window)) return;
    const sendState = () => {
      if (!window.webContents.isDestroyed()) window.webContents.send('workbench:shell-maximized-changed', window.isMaximized());
    };
    const cleanup = () => {
      window.off('maximize', sendState);
      window.off('unmaximize', sendState);
      windowCleanups.delete(window);
    };
    window.on('maximize', sendState);
    window.on('unmaximize', sendState);
    window.once('closed', cleanup);
    windowCleanups.set(window, cleanup);
  };
  const onBrowserWindowCreated = (_event: Electron.Event, window: BrowserWindow) => bindWindow(window);

  BrowserWindow.getAllWindows().forEach(bindWindow);
  app.on('browser-window-created', onBrowserWindowCreated);
  ipcMain.handle('shell:minimize-window', (event) => BrowserWindow.fromWebContents(event.sender)?.minimize());
  ipcMain.handle('shell:toggle-maximize-window', (event) => {
    const window = BrowserWindow.fromWebContents(event.sender);
    if (!window) return;
    if (window.isMaximized()) window.unmaximize(); else window.maximize();
  });
  ipcMain.handle('shell:close-window', (event) => BrowserWindow.fromWebContents(event.sender)?.close());
  ipcMain.handle('shell:is-maximized', (event) => BrowserWindow.fromWebContents(event.sender)?.isMaximized() ?? false);

  return () => {
    app.off('browser-window-created', onBrowserWindowCreated);
    windowCleanups.forEach((cleanup) => cleanup());
    SHELL_CHANNELS.forEach((channel) => ipcMain.removeHandler(channel));
  };
}
