import { describe, expect, it, vi } from 'vitest';
import { join } from 'node:path';
import {
  WorkbenchTrayController,
  type WorkbenchTrayMenuItem,
  type WorkbenchTray
} from '../../src/main/services/workbench-tray-controller';

describe('Workbench 托盘控制器', () => {
  it('托盘点击和打开工作台菜单共用恢复入口，退出菜单只请求 app.quit', () => {
    const tray = new FakeTray();
    const restoreMainWindow = vi.fn();
    const quit = vi.fn();
    let menu: readonly WorkbenchTrayMenuItem[] = [];

    new WorkbenchTrayController({
      createTray: (iconPath) => {
        expect(iconPath).toBe(join('C:/resources', 'tray', 'app-icon.ico'));
        return tray;
      },
      buildContextMenu: (template) => { menu = template; return template; },
      restoreMainWindow,
      quit,
      isPackaged: true,
      resourcesPath: 'C:/resources',
      appPath: 'D:/workbench'
    });

    expect(tray.tooltip).toBe('Workbench');
    expect(menu[0]?.label).toBe('打开工作台');
    expect(menu[2]?.label).toBe('退出');

    tray.click();
    menu[0]?.click();
    menu[2]?.click();

    expect(restoreMainWindow).toHaveBeenCalledTimes(2);
    expect(quit).toHaveBeenCalledOnce();
  });

  it('开发环境使用应用目录下的 assets/app-icon.ico，销毁时释放托盘', () => {
    const tray = new FakeTray();

    const controller = new WorkbenchTrayController({
      createTray: (iconPath) => {
        expect(iconPath).toBe(join('D:/workbench', 'assets', 'app-icon.ico'));
        return tray;
      },
      buildContextMenu: (template) => template,
      restoreMainWindow: () => undefined,
      quit: () => undefined,
      isPackaged: false,
      resourcesPath: 'C:/resources',
      appPath: 'D:/workbench'
    });

    controller.destroy();
    controller.destroy();

    expect(tray.destroyCalls).toBe(1);
  });

  it('创建托盘失败时报告中文错误并暴露不可用状态', () => {
    const errors: string[] = [];
    const controller = new WorkbenchTrayController({
      createTray: () => { throw new Error('系统托盘不可用'); },
      buildContextMenu: (template) => template,
      restoreMainWindow: () => undefined,
      quit: () => undefined,
      isPackaged: false,
      resourcesPath: 'C:/resources',
      appPath: 'D:/workbench',
      logger: { error: (message) => errors.push(message) }
    });

    expect(controller.isAvailable()).toBe(false);
    expect(errors).toEqual(['创建 Workbench 托盘失败：系统托盘不可用']);
  });
});

class FakeTray implements WorkbenchTray {
  public tooltip?: string;
  public destroyCalls = 0;
  private clickHandler?: () => void;

  public on(event: 'click', listener: () => void): this {
    if (event === 'click') this.clickHandler = listener;
    return this;
  }

  public setContextMenu(_menu: unknown): void {}
  public setToolTip(tooltip: string): void { this.tooltip = tooltip; }
  public destroy(): void { this.destroyCalls += 1; }
  public click(): void { this.clickHandler?.(); }
}
