import { beforeEach, describe, expect, it, vi } from 'vitest';

const electronMock = vi.hoisted(() => {
  const handlers = new Map<string, (...args: any[]) => unknown>();
  const appListeners = new Map<string, (...args: any[]) => void>();
  const windows = new Map<object, any>();
  return {
    handlers,
    appListeners,
    windows,
    app: {
      on: vi.fn((event: string, listener: (...args: any[]) => void) => appListeners.set(event, listener)),
      off: vi.fn((event: string, listener: (...args: any[]) => void) => {
        if (appListeners.get(event) === listener) appListeners.delete(event);
      })
    },
    ipcMain: {
      handle: vi.fn((channel: string, handler: (...args: any[]) => unknown) => handlers.set(channel, handler)),
      removeHandler: vi.fn((channel: string) => handlers.delete(channel))
    },
    BrowserWindow: {
      getAllWindows: vi.fn(() => []),
      fromWebContents: vi.fn((sender: object) => windows.get(sender))
    }
  };
});

vi.mock('electron', () => ({ app: electronMock.app, ipcMain: electronMock.ipcMain, BrowserWindow: electronMock.BrowserWindow }));

import { registerWindowShellIpc } from '../../src/main/services/window-shell-ipc';

describe('原生窗口 Shell IPC', () => {
  beforeEach(() => {
    electronMock.handlers.clear();
    electronMock.appListeners.clear();
    electronMock.windows.clear();
    electronMock.app.on.mockClear();
    electronMock.app.off.mockClear();
    electronMock.ipcMain.handle.mockClear();
    electronMock.ipcMain.removeHandler.mockClear();
  });

  it('最小化、切换最大化和关闭严格作用于各自发送窗口', async () => {
    const firstSender = {};
    const secondSender = {};
    const firstWindow = createWindow();
    const secondWindow = createWindow();
    secondWindow.maximized = true;
    electronMock.windows.set(firstSender, firstWindow);
    electronMock.windows.set(secondSender, secondWindow);
    const unregister = registerWindowShellIpc();

    expect(await electronMock.handlers.get('shell:is-maximized')?.({ sender: firstSender })).toBe(false);
    expect(await electronMock.handlers.get('shell:is-maximized')?.({ sender: secondSender })).toBe(true);
    await electronMock.handlers.get('shell:minimize-window')?.({ sender: firstSender });
    await electronMock.handlers.get('shell:toggle-maximize-window')?.({ sender: firstSender });
    await electronMock.handlers.get('shell:toggle-maximize-window')?.({ sender: secondSender });
    await electronMock.handlers.get('shell:close-window')?.({ sender: secondSender });

    expect(firstWindow.minimize).toHaveBeenCalledOnce();
    expect(firstWindow.maximize).toHaveBeenCalledOnce();
    expect(firstWindow.unmaximize).not.toHaveBeenCalled();
    expect(firstWindow.close).not.toHaveBeenCalled();
    expect(secondWindow.minimize).not.toHaveBeenCalled();
    expect(secondWindow.maximize).not.toHaveBeenCalled();
    expect(secondWindow.unmaximize).toHaveBeenCalledOnce();
    expect(secondWindow.close).toHaveBeenCalledOnce();
    unregister();
  });

  it('转发各窗口原生最大化事件，并在注销后停止发送', () => {
    const firstWindow = createWindow();
    const secondWindow = createWindow();
    const unregister = registerWindowShellIpc();
    electronMock.appListeners.get('browser-window-created')?.({}, firstWindow);
    electronMock.appListeners.get('browser-window-created')?.({}, secondWindow);

    firstWindow.maximized = true;
    firstWindow.emit('maximize');
    secondWindow.maximized = false;
    secondWindow.emit('unmaximize');

    expect(firstWindow.webContents.send).toHaveBeenCalledWith('workbench:shell-maximized-changed', true);
    expect(secondWindow.webContents.send).toHaveBeenCalledWith('workbench:shell-maximized-changed', false);
    unregister();
    firstWindow.emit('unmaximize');
    secondWindow.emit('maximize');

    expect(firstWindow.webContents.send).toHaveBeenCalledTimes(1);
    expect(secondWindow.webContents.send).toHaveBeenCalledTimes(1);
  });
});

function createWindow() {
  const listeners = new Map<string, Set<() => void>>();
  return {
    maximized: false,
    webContents: { send: vi.fn(), isDestroyed: vi.fn(() => false) },
    on(event: string, listener: () => void) {
      const current = listeners.get(event) ?? new Set();
      current.add(listener);
      listeners.set(event, current);
      return this;
    },
    once(event: string, listener: () => void) { return this.on(event, listener); },
    off(event: string, listener: () => void) { listeners.get(event)?.delete(listener); return this; },
    emit(event: string) { listeners.get(event)?.forEach((listener) => listener()); },
    isMaximized() { return this.maximized; },
    isMinimized: vi.fn(() => false),
    minimize: vi.fn(), restore: vi.fn(), maximize: vi.fn(), unmaximize: vi.fn(), close: vi.fn()
  };
}
