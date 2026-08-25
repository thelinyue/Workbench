import { app, BrowserWindow, Menu } from 'electron';
import { join } from 'node:path';
import { registerWorkbenchIpc } from './ipc';
import { registerAppResourceProtocol } from './services/app-resource-protocol';

let closeWorkbench: (() => void) | undefined;
let unregisterAppProtocol: (() => void) | undefined;

/**
 * Electron 应用壳层只创建一个原生窗口。
 * 应用桌面和分析中心等“窗口”由 React 在这个安全的单渲染上下文内管理，避免多 BrowserWindow
 * 的状态分裂与额外 IPC 面。
 */
function createWindow(): void {
  const window = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1024,
    minHeight: 680,
    center: true,
    show: false,
    title: '工作台',
    backgroundColor: '#EAFBF5',
    frame: false,
    webPreferences: {
      preload: join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });

  if (process.env.ELECTRON_RENDERER_URL) window.loadURL(process.env.ELECTRON_RENDERER_URL);
  else window.loadFile(join(__dirname, '../renderer/index.html'));

  // 等待渲染层完成首帧后再显示，确保固定尺寸窗口不会在首帧加载过程中闪烁。
  window.once('ready-to-show', () => {
    window.show();
  });
}

app.whenReady().then(() => {
  // Windows 菜单栏会与自绘 Desktop Shell 重复，工作台只保留统一的顶栏与窗口控制。
  Menu.setApplicationMenu(null);
  unregisterAppProtocol = registerAppResourceProtocol(join(app.getPath('userData'), 'Workbench', 'apps'));
  closeWorkbench = registerWorkbenchIpc(app.getPath('userData'));
  createWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
});

app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
app.on('will-quit', () => { unregisterAppProtocol?.(); closeWorkbench?.(); });
