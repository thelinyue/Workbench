import { access, mkdir } from 'node:fs/promises';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { BrowserWindow, dialog, ipcMain, shell } from 'electron';
import { z } from 'zod';
import { WorkspaceRepository } from './data/workspace-repository';
import { AnalysisCenterService } from './services/analysis-center-service';
import { AnalysisTaskService } from './services/analysis-task-service';
import { LifecycleDeletionService } from './services/lifecycle-deletion-service';
import { MonitorDirectoryWatcher } from './services/monitor-directory-watcher';

const idSchema = z.string().uuid();
const idListSchema = z.array(idSchema).min(1).max(200);
const importPathSchema = z.array(z.string().min(1).max(2048)).min(1).max(200);
const layoutSchema = z.array(z.object({ appId: z.enum(['analysis-center', 'settings']), x: z.number().int().min(0).max(5000), y: z.number().int().min(0).max(5000) }));
const directorySchema = z.array(z.string().min(1).max(2048)).max(30);
const confirmedDeletionSchema = z.object({ packageIds: idListSchema, confirmationToken: z.string().uuid() });

/**
 * 注册工作台全部受控 IPC 入口。
 *
 * 每个参数均在主进程进行 Zod 校验；渲染层传来的 id 还会再次解析为仓储对象，防止页面脚本
 * 伪造任意本机路径触发文件删除、浏览器打开或资源管理器定位。
 */
export function registerWorkbenchIpc(userDataPath: string): () => void {
  const dataDirectory = join(userDataPath, 'Workbench');
  const repository = new WorkspaceRepository(join(dataDirectory, 'workbench.db'));
  const analysis = new AnalysisCenterService(repository);
  const tasks = new AnalysisTaskService(repository);
  const deletion = new LifecycleDeletionService(repository);
  const notifyRenderer = () => BrowserWindow.getAllWindows().forEach((window) => window.webContents.send('workbench:changed'));
  const monitor = new MonitorDirectoryWatcher(analysis, notifyRenderer);
  const deletionConfirmations = new Map<string, { packageIds: string[]; webContentsId: number; expiresAt: number }>();
  monitor.watch(repository.getMonitorDirectories());

  const getPackage = (input: unknown) => {
    const id = idSchema.parse(input);
    const item = analysis.getPackage(id);
    if (!item) throw new Error('找不到指定的诊断包');
    return item;
  };
  const getPackages = (input: unknown) => idListSchema.parse(input).map((id) => getPackage(id));
  const ensurePath = async (path: string, message: string) => { await access(path).catch(() => { throw new Error(message); }); };

  ipcMain.handle('desktop:load-layout', () => repository.listDesktopLayout());
  ipcMain.handle('desktop:save-layout', (_event, input) => repository.saveDesktopLayout(layoutSchema.parse(input)));
  ipcMain.handle('analysis:list', () => analysis.listPackages());
  ipcMain.handle('analysis:scan', async () => { const result = await analysis.scanMonitorDirectories(); notifyRenderer(); return result; });
  ipcMain.handle('analysis:import-package', async () => {
    const result = await dialog.showOpenDialog({ properties: ['openFile'], filters: [{ name: '诊断包', extensions: ['tgz', 'temp'] }] });
    if (result.canceled || !result.filePaths[0]) return null;
    const item = await analysis.importPackage(result.filePaths[0]);
    notifyRenderer();
    return item;
  });
  ipcMain.handle('analysis:import-paths', async (_event, input) => {
    const items = [];
    for (const path of importPathSchema.parse(input)) items.push(await analysis.importPackage(path));
    notifyRenderer();
    return items;
  });
  ipcMain.handle('analysis:start', async (_event, input) => { await tasks.enqueue(getPackage(input).id); });
  ipcMain.handle('analysis:start-all-pending', () => tasks.enqueueAllPending());
  ipcMain.handle('analysis:open-report', async (_event, input) => {
    const item = getPackage(input);
    if (!item.reportPath) throw new Error('该诊断包尚未生成报告');
    await ensurePath(item.reportPath, '报告文件不存在，可能已被删除');
    const result = await shell.openPath(item.reportPath);
    if (result) throw new Error(`无法打开报告：${result}`);
  });
  ipcMain.handle('analysis:locate-source', async (_event, input) => {
    const item = getPackage(input);
    await ensurePath(item.sourcePath, '诊断包文件不存在，无法定位');
    shell.showItemInFolder(item.sourcePath);
  });
  ipcMain.handle('analysis:locate-extract', async (_event, input) => {
    const item = getPackage(input);
    await ensurePath(item.extractPath, '解压目录不存在，无法定位');
    shell.showItemInFolder(item.extractPath);
  });
  ipcMain.handle('analysis:delete-preview', async (event, input) => {
    const packageIds = idListSchema.parse(input);
    const preview = await deletion.preview(getPackages(packageIds));
    const confirmationToken = randomUUID();
    deletionConfirmations.set(confirmationToken, { packageIds, webContentsId: event.sender.id, expiresAt: Date.now() + 5 * 60_000 });
    return { ...preview, confirmationToken };
  });
  ipcMain.handle('analysis:delete-packages', async (event, input) => {
    const { packageIds, confirmationToken } = confirmedDeletionSchema.parse(input);
    const confirmation = deletionConfirmations.get(confirmationToken);
    deletionConfirmations.delete(confirmationToken);
    if (!confirmation || confirmation.expiresAt < Date.now() || confirmation.webContentsId !== event.sender.id || confirmation.packageIds.length !== packageIds.length || confirmation.packageIds.some((id, index) => id !== packageIds[index])) {
      throw new Error('删除确认已失效，请重新查看删除清单后确认');
    }
    await deletion.delete(getPackages(packageIds));
    notifyRenderer();
  });
  ipcMain.handle('tasks:list', () => repository.listTasks());
  ipcMain.handle('tasks:cancel', (_event, input) => tasks.cancel(idSchema.parse(input)));
  ipcMain.handle('settings:get-monitor-directories', () => repository.getMonitorDirectories());
  ipcMain.handle('settings:save-monitor-directories', async (_event, input) => {
    const directories = directorySchema.parse(input);
    await Promise.all(directories.map((directory) => mkdir(directory, { recursive: true })));
    repository.saveMonitorDirectories(directories);
    monitor.watch(directories);
  });
  ipcMain.handle('settings:choose-monitor-directory', async () => {
    const result = await dialog.showOpenDialog({ properties: ['openDirectory', 'createDirectory'] });
    return result.canceled ? null : result.filePaths[0] ?? null;
  });

  tasks.on('changed', notifyRenderer);
  return () => { tasks.off('changed', notifyRenderer); void monitor.close(); repository.close(); };
}
