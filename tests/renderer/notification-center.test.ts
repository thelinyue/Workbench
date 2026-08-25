import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
const stylesSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

describe('全局消息中心', () => {
  it('在工作台顶栏提供消息中心入口和未读红点', () => {
    expect(appSource).toContain('消息中心');
    expect(appSource).toContain('unreadNotificationCount');
    expect(appSource).toContain('notification-center');
    expect(stylesSource).toContain('.notification-center');
  });

  it('从全局状态变更中聚合诊断包和应用更新消息', () => {
    expect(appSource).toContain('collectNewNotifications');
    expect(appSource).toContain('notificationSnapshotRef');
    expect(appSource).toContain("window.workbench.apps.invoke('analysis-center', 'packages.list')");
    expect(appSource).toContain('window.workbench.apps.onEvent');
    expect(appSource).toContain('update-available');
  });

  it('消息点击后打开对应应用并标记已读', () => {
    expect(appSource).toContain('handleNotificationClick');
    expect(appSource).toContain('setNotificationCenterOpen(false)');
    expect(appSource).toContain('target?.type === \'analysis-package\'');
    expect(appSource).toContain('target?.type === \'app\'');
  });
});
