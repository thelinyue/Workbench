import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import { afterEach, describe, expect, it } from 'vitest';
import { AppRegistryRepository } from '../../src/main/data/app-registry-repository';
import type { AppCatalogSnapshot, AppInstallRecord } from '../../src/shared/app-contract';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

describe('应用注册表仓储', () => {
  it('持久化已安装应用和最后一次有效目录缓存', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-registry-'));
    directories.push(root);
    const record: AppInstallRecord = {
      id: 'analysis-center',
      name: '分析中心',
      description: '诊断包与日志报告',
      publisherId: 'thelinyue',
      installedVersion: '1.0.0',
      activeVersion: '1.0.0',
      installPath: join(root, 'apps', 'analysis-center', '1.0.0'),
      enabled: true,
      state: 'installed'
    };
    const snapshot: AppCatalogSnapshot = {
      catalog: { schemaVersion: 1, apps: [] },
      fetchedAt: '2026-08-26T00:00:00.000Z',
      fromCache: false
    };

    const first = new AppRegistryRepository(join(root, 'apps.db'));
    first.upsert(record);
    first.saveCatalogSnapshot(snapshot);
    first.close();

    const second = new AppRegistryRepository(join(root, 'apps.db'));
    expect(second.list()).toEqual([record]);
    expect(second.loadCatalogSnapshot()).toEqual(snapshot);
    second.close();
  });

  it('为旧注册表迁移 enabled 字段，并严格映射 0/1 布尔值', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-registry-'));
    directories.push(root);
    const databasePath = join(root, 'apps.db');
    const legacy = new DatabaseSync(databasePath);
    legacy.exec(`
      CREATE TABLE installed_apps (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        description TEXT NOT NULL,
        publisher_id TEXT NOT NULL,
        installed_version TEXT,
        available_version TEXT,
        active_version TEXT,
        install_path TEXT,
        state TEXT NOT NULL,
        error_message TEXT
      )
    `);
    legacy.close();

    const repository = new AppRegistryRepository(databasePath);
    repository.upsert({
      id: 'analysis-center',
      name: '分析中心',
      description: '诊断包与日志报告',
      publisherId: 'thelinyue',
      enabled: false,
      state: 'installed'
    });
    expect(repository.get('analysis-center')?.enabled).toBe(false);
    repository.setEnabled('analysis-center', true);
    expect(repository.get('analysis-center')?.enabled).toBe(true);
    repository.setEnabled('analysis-center', false);
    expect(repository.get('analysis-center')?.enabled).toBe(false);

    const migrated = new DatabaseSync(databasePath);
    const columns = migrated.prepare('PRAGMA table_info(installed_apps)').all() as Array<{ name: string }>;
    expect(columns.filter((column) => column.name === 'enabled')).toHaveLength(1);
    migrated.close();
    repository.remove('analysis-center');
    expect(repository.get('analysis-center')).toBeUndefined();
    repository.close();
  });

  it('新表中省略 enabled 时默认为启用', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-registry-'));
    directories.push(root);
    const databasePath = join(root, 'apps.db');
    const repository = new AppRegistryRepository(databasePath);
    repository.close();

    const database = new DatabaseSync(databasePath);
    database.prepare(`
      INSERT INTO installed_apps (id, name, description, publisher_id, state)
      VALUES (?, ?, ?, ?, ?)
    `).run('terminal', 'SSH 终端', '远程终端', 'thelinyue', 'installed');
    database.close();

    const reopened = new AppRegistryRepository(databasePath);
    expect(reopened.get('terminal')?.enabled).toBe(true);
    reopened.close();
  });
});
