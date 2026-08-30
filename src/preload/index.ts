import { contextBridge, ipcRenderer, webUtils } from 'electron';

/**
 * 预加载层是渲染进程访问本地能力的唯一入口。
 * 所有文件系统、数据库、任务和浏览器操作都必须由主进程二次校验，页面脚本不能直接使用 Node API。
 */
contextBridge.exposeInMainWorld('workbench', {
  desktop: {
    initializeLayout: (defaultLayout: unknown) => ipcRenderer.invoke('desktop:initialize-layout', defaultLayout),
    saveLayout: (layout: unknown) => ipcRenderer.invoke('desktop:save-layout', layout)
  },
  shell: {
    minimize: () => ipcRenderer.invoke('shell:minimize-window'),
    toggleMaximize: () => ipcRenderer.invoke('shell:toggle-maximize-window'),
    close: () => ipcRenderer.invoke('shell:close-window'),
    isMaximized: () => ipcRenderer.invoke('shell:is-maximized'),
    onMaximizedChanged: (listener: (maximized: boolean) => void) => {
      const channel = 'workbench:shell-maximized-changed';
      const callback = (_event: Electron.IpcRendererEvent, value: unknown) => listener(value === true);
      ipcRenderer.on(channel, callback);
      return () => ipcRenderer.removeListener(channel, callback);
    }
  },
  appWindow: {
    getContext: () => ipcRenderer.invoke('app-window:get-context'),
    markEventSurfaceReady: () => ipcRenderer.invoke('app-window:event-surface-ready')
  },
  apps: {
    list: () => ipcRenderer.invoke('apps:list'),
    refreshCatalog: () => ipcRenderer.invoke('apps:refresh-catalog'),
    install: (appId: string, version?: string) => ipcRenderer.invoke('apps:install', { appId, version }),
    launch: (appId: string) => ipcRenderer.invoke('apps:launch', appId),
    reload: (appId: string) => ipcRenderer.invoke('apps:reload', appId),
    getEntryUrl: (appId: string) => ipcRenderer.invoke('apps:get-entry-url', appId),
    invoke: (appId: string, method: string, payload?: unknown) => ipcRenderer.invoke('apps:invoke', { appId, method, payload }),
    setEnabled: (appId: string, enabled: boolean) => ipcRenderer.invoke('apps:set-enabled', { appId, enabled }),
    uninstall: (appId: string, deleteData: boolean) => ipcRenderer.invoke('apps:uninstall', { appId, deleteData }),
    getDroppedFilePaths: (files: File[]) => files.map((file) => webUtils.getPathForFile(file)).filter(Boolean),
    getCatalogSnapshot: () => ipcRenderer.invoke('apps:get-catalog-snapshot'),
    onEvent: (listener: (event: unknown) => void) => {
      const channel = 'workbench:app-event';
      const callback = (_event: Electron.IpcRendererEvent, value: unknown) => listener(value);
      ipcRenderer.on(channel, callback);
      return () => ipcRenderer.removeListener(channel, callback);
    }
  },
  onChanged: (listener: () => void) => { const channel = 'workbench:changed'; const callback = () => listener(); ipcRenderer.on(channel, callback); return () => ipcRenderer.removeListener(channel, callback); }
});
