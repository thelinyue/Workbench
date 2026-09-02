import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { AppWindowStateRepository } from '../../src/main/data/app-window-state-repository';

const temporaryDirectories: string[] = [];

afterEach(async () => {
  await Promise.all(temporaryDirectories.splice(0).map((path) => rm(path, { recursive: true, force: true })));
});

async function createRepository(): Promise<AppWindowStateRepository> {
  const root = await mkdtemp(join(tmpdir(), 'workbench-app-window-state-'));
  temporaryDirectories.push(root);
  return new AppWindowStateRepository(join(root, 'workbench.db'));
}

describe('应用窗口状态仓储', () => {
  it('不存在保存状态时返回 undefined', async () => {
    const repository = await createRepository();

    expect(repository.load('analysis-center', 'main')).toBeUndefined();
    repository.close();
  });

  it('保存后可从真实 SQLite 读取完整普通边界和最大化状态', async () => {
    const repository = await createRepository();

    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 120, y: 80, width: 1200, height: 800, maximized: true });

    expect(repository.load('analysis-center', 'main')).toEqual({
      appId: 'analysis-center', windowKey: 'main', x: 120, y: 80, width: 1200, height: 800, maximized: true
    });
    repository.close();
  });

  it('相同复合键再次保存会替换旧状态', async () => {
    const repository = await createRepository();
    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 10, y: 20, width: 900, height: 600, maximized: false });

    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 30, y: 40, width: 1100, height: 700, maximized: true });

    expect(repository.load('analysis-center', 'main')).toMatchObject({ x: 30, y: 40, width: 1100, height: 700, maximized: true });
    repository.close();
  });

  it('同一应用的不同 windowKey 分别保存', async () => {
    const repository = await createRepository();
    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 10, y: 20, width: 900, height: 600, maximized: false });
    repository.upsert({ appId: 'analysis-center', windowKey: 'evidence', x: 100, y: 200, width: 800, height: 560, maximized: false });

    expect(repository.load('analysis-center', 'main')?.x).toBe(10);
    expect(repository.load('analysis-center', 'evidence')?.x).toBe(100);
    repository.close();
  });

  it('一次性迁移只删除 analysis-center/main，重启后不会删除后续写入的状态', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-window-state-migration-'));
    temporaryDirectories.push(root);
    const databasePath = join(root, 'workbench.db');
    const migrationId = 'analysis-center-window-state-reset-v1';
    const repository = new AppWindowStateRepository(databasePath);
    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 10, y: 20, width: 1500, height: 900, maximized: true });
    repository.upsert({ appId: 'analysis-center', windowKey: 'evidence', x: 30, y: 40, width: 900, height: 600, maximized: false });
    repository.upsert({ appId: 'other-app', windowKey: 'main', x: 50, y: 60, width: 1000, height: 700, maximized: false });

    repository.resetStateOnce(migrationId, 'analysis-center', 'main');

    expect(repository.load('analysis-center', 'main')).toBeUndefined();
    expect(repository.load('analysis-center', 'evidence')).toMatchObject({ x: 30, y: 40, width: 900, height: 600 });
    expect(repository.load('other-app', 'main')).toMatchObject({ x: 50, y: 60, width: 1000, height: 700 });
    repository.upsert({ appId: 'analysis-center', windowKey: 'main', x: 70, y: 80, width: 800, height: 560, maximized: false });
    repository.close();

    const reopenedRepository = new AppWindowStateRepository(databasePath);
    reopenedRepository.resetStateOnce(migrationId, 'analysis-center', 'main');

    expect(reopenedRepository.load('analysis-center', 'main')).toMatchObject({ x: 70, y: 80, width: 800, height: 560 });
    expect(reopenedRepository.load('analysis-center', 'evidence')).toMatchObject({ x: 30, y: 40, width: 900, height: 600 });
    reopenedRepository.close();
  });
});
