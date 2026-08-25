import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { WorkspaceRepository } from '../../src/main/data/workspace-repository';

const directories: string[] = [];

afterEach(async () => {
  await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe('工作台 SQLite 数据仓储', () => {
  it('持久化诊断包、任务和桌面布局', async () => {
    const dataDirectory = await mkdtemp(join(tmpdir(), 'workbench-data-'));
    directories.push(dataDirectory);
    const databasePath = join(dataDirectory, 'workbench.db');
    const repository = new WorkspaceRepository(databasePath);

    repository.saveDesktopLayout([{ appId: 'analysis-center', x: 56, y: 88 }]);
    repository.upsertPackage({
      id: 'package-1',
      sourcePath: 'D:/Inbox/core-048.tgz',
      extractPath: 'D:/Inbox/core-048',
      reportPath: undefined,
      displayName: 'core-048.tgz',
      detectedAt: '2026-08-25T10:25:00.000Z',
      status: 'pending',
      taskIds: [],
      caseId: 'case-1'
    });
    repository.upsertTask({
      id: 'task-1',
      packageId: 'package-1',
      status: 'queued',
      createdAt: '2026-08-25T10:25:01.000Z',
      progress: 0,
      message: '等待分析'
    });

    expect(repository.listDesktopLayout()).toEqual([{ appId: 'analysis-center', x: 56, y: 88 }]);
    expect(repository.listPackages()).toHaveLength(1);
    expect(repository.listTasks()).toEqual([expect.objectContaining({ id: 'task-1', packageId: 'package-1' })]);

    repository.close();
  });
});
