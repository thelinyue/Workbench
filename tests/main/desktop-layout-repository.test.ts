import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { DesktopLayoutRepository } from '../../src/main/data/desktop-layout-repository';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

describe('工作台桌面布局仓储', () => {
  it('只持久化桌面图标坐标并自动创建数据库目录', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-layout-'));
    directories.push(root);
    const repository = new DesktopLayoutRepository(join(root, 'created-later', 'workbench.db'));

    repository.save([{ appId: 'analysis-center', x: 56, y: 88 }, { appId: 'app-center', x: 120, y: 88 }]);

    expect(repository.list()).toEqual([
      { appId: 'analysis-center', x: 56, y: 88 },
      { appId: 'app-center', x: 120, y: 88 }
    ]);
    repository.close();
  });
});
