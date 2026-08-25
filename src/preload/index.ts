import { contextBridge, ipcRenderer } from 'electron';

/**
 * 预加载层是渲染进程访问本地能力的唯一入口。
 * 所有文件系统、数据库、任务和浏览器操作都必须由主进程二次校验，页面脚本不能直接使用 Node API。
 */
contextBridge.exposeInMainWorld('workbench', {
  desktop: { loadLayout: () => ipcRenderer.invoke('desktop:load-layout'), saveLayout: (layout: unknown) => ipcRenderer.invoke('desktop:save-layout', layout) },
  shell: { minimize: () => ipcRenderer.invoke('shell:minimize-window'), toggleMaximize: () => ipcRenderer.invoke('shell:toggle-maximize-window'), close: () => ipcRenderer.invoke('shell:close-window') },
  apps: {
    list: () => ipcRenderer.invoke('apps:list'),
    refreshCatalog: () => ipcRenderer.invoke('apps:refresh-catalog'),
    install: (appId: string, version?: string) => ipcRenderer.invoke('apps:install', { appId, version }),
    launch: (appId: string) => ipcRenderer.invoke('apps:launch', appId),
    getEntryUrl: (appId: string) => ipcRenderer.invoke('apps:get-entry-url', appId),
    invoke: (appId: string, method: string, payload?: unknown) => ipcRenderer.invoke('apps:invoke', { appId, method, payload }),
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
