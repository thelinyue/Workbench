import { join } from 'node:path';

export interface WorkbenchTray {
  on(event: 'click', listener: () => void): this;
  setContextMenu(menu: unknown): void;
  setToolTip(tooltip: string): void;
  destroy(): void;
}

export interface WorkbenchTrayMenuItem {
  label: string;
  type?: 'normal' | 'separator';
  click: () => void;
}

export interface WorkbenchTrayControllerOptions {
  createTray(iconPath: string): WorkbenchTray;
  buildContextMenu(template: readonly WorkbenchTrayMenuItem[]): unknown;
  restoreMainWindow(): void;
  quit(): void;
  isPackaged: boolean;
  resourcesPath: string;
  appPath: string;
  logger?: Pick<Console, 'error'>;
}

/**
 * 管理 Workbench 的托盘驻留入口。
 *
 * 托盘 click 与“打开工作台”菜单严格复用同一个恢复回调，避免两个入口在窗口已隐藏、
 * 最小化或已被销毁时出现不同的恢复行为。“退出”只请求 app.quit()，异步资源收口仍由
 * WorkbenchLifecycleController 的 before-quit gate 统一负责，托盘本身不会绕过生命周期。
 */
export class WorkbenchTrayController {
  private tray: WorkbenchTray | undefined;

  public constructor(private readonly options: WorkbenchTrayControllerOptions) {
    try {
      this.tray = options.createTray(resolveWorkbenchTrayIconPath(options));
      this.tray.setToolTip('Workbench');
      const restoreMainWindow = options.restoreMainWindow;
      this.tray.on('click', restoreMainWindow);
      this.tray.setContextMenu(options.buildContextMenu([
        { label: '打开工作台', click: restoreMainWindow },
        { label: '', type: 'separator', click: () => undefined },
        { label: '退出', click: options.quit }
      ]));
    } catch (error) {
      // 托盘创建成功后，后续初始化失败仍需释放局部资源；清理异常只能记录，不能覆盖原始错误。
      const tray = this.tray;
      this.tray = undefined;
      if (tray) {
        try { tray.destroy(); }
        catch (cleanupError) { (options.logger ?? console).error(`清理 Workbench 托盘失败：${errorMessage(cleanupError)}`); }
      }
      (options.logger ?? console).error(`创建 Workbench 托盘失败：${errorMessage(error)}`);
    }
  }

  /** 托盘创建失败时返回 false，生命周期可据此避免把主窗口隐藏成不可恢复的后台进程。 */
  public isAvailable(): boolean {
    return this.tray !== undefined;
  }

  /**
   * 最终退出时销毁托盘图标。销毁必须幂等，并且不能让托盘异常阻断其他资源的有序收口。
   */
  public destroy(): void {
    const tray = this.tray;
    if (!tray) return;
    this.tray = undefined;
    try { tray.destroy(); }
    catch (error) { (this.options.logger ?? console).error(`销毁 Workbench 托盘失败：${errorMessage(error)}`); }
  }
}

/** 严格区分开发资源与打包资源，避免打包版从源码目录读取图标。 */
export function resolveWorkbenchTrayIconPath(options: Pick<WorkbenchTrayControllerOptions, 'isPackaged' | 'resourcesPath' | 'appPath'>): string {
  return options.isPackaged
    ? join(options.resourcesPath, 'tray', 'app-icon.ico')
    : join(options.appPath, 'assets', 'app-icon.ico');
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
