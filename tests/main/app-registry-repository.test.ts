import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
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
});
