import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

describe('任务中心清理操作', () => {
  it('提供单项清理和一键清理历史任务入口', () => {
    expect(appSource).toContain("window.workbench.apps.invoke(task.appId, 'tasks.clear'");
    expect(appSource).toContain("window.workbench.apps.invoke(appId, 'tasks.clear-completed'");
    expect(appSource).toContain('一键清理');
    expect(appSource).toContain('确定一键清理');
  });

  it('仅允许终态任务清理，运行中和排队任务仍提供取消操作', () => {
    expect(appSource).toContain("task.status === 'succeeded' || task.status === 'failed' || task.status === 'cancelled'");
    expect(appSource).toContain('onCancel(task.id)');
    expect(appSource).toContain('清理期间');
  });
});
