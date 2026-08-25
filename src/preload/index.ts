import { contextBridge, ipcRenderer, webUtils } from 'electron';

/**
 * 预加载层是渲染进程访问本地能力的唯一入口。
 * 所有文件系统、数据库、任务和浏览器操作都必须由主进程二次校验，页面脚本不能直接使用 Node API。
 */
contextBridge.exposeInMainWorld('workbench', {
  desktop: { loadLayout: () => ipcRenderer.invoke('desktop:load-layout'), saveLayout: (layout: unknown) => ipcRenderer.invoke('desktop:save-layout', layout) },
  shell: { minimize: () => ipcRenderer.invoke('shell:minimize-window'), toggleMaximize: () => ipcRenderer.invoke('shell:toggle-maximize-window'), close: () => ipcRenderer.invoke('shell:close-window') },
  analysis: {
    list: () => ipcRenderer.invoke('analysis:list'), importPackage: () => ipcRenderer.invoke('analysis:import-package'),
    importDroppedFiles: (files: File[]) => ipcRenderer.invoke('analysis:import-paths', files.map((file) => webUtils.getPathForFile(file))), scan: () => ipcRenderer.invoke('analysis:scan'),
    start: (packageId: string) => ipcRenderer.invoke('analysis:start', packageId), startAllPending: () => ipcRenderer.invoke('analysis:start-all-pending'),
    openReport: (packageId: string) => ipcRenderer.invoke('analysis:open-report', packageId), locateSource: (packageId: string) => ipcRenderer.invoke('analysis:locate-source', packageId),
    locateExtract: (packageId: string) => ipcRenderer.invoke('analysis:locate-extract', packageId), deletePreview: (packageIds: string[]) => ipcRenderer.invoke('analysis:delete-preview', packageIds),
    deletePackages: (packageIds: string[], confirmationToken: string) => ipcRenderer.invoke('analysis:delete-packages', { packageIds, confirmationToken })
  },
  tasks: { list: () => ipcRenderer.invoke('tasks:list'), cancel: (taskId: string) => ipcRenderer.invoke('tasks:cancel', taskId) },
  settings: { getMonitorDirectories: () => ipcRenderer.invoke('settings:get-monitor-directories'), saveMonitorDirectories: (directories: string[]) => ipcRenderer.invoke('settings:save-monitor-directories', directories), chooseMonitorDirectory: () => ipcRenderer.invoke('settings:choose-monitor-directory') },
  onChanged: (listener: () => void) => { const channel = 'workbench:changed'; const callback = () => listener(); ipcRenderer.on(channel, callback); return () => ipcRenderer.removeListener(channel, callback); }
});
