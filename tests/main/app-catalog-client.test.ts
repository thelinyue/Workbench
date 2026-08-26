import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { AppRegistryRepository } from '../../src/main/data/app-registry-repository';
import { AppCatalogClient, selectLatestCompatibleAppRelease, type AppHttpResponse } from '../../src/main/services/app-catalog-client';
import type { AppCatalogDocumentV1, AppCatalogItem } from '../../src/shared/app-contract';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

const catalog: AppCatalogDocumentV1 = {
  schemaVersion: 1,
  apps: [{
    id: 'analysis-center',
    name: '分析中心',
    description: '诊断包与日志报告',
    publisherId: 'thelinyue',
    releases: [
      { version: '1.0.0', hostApiVersion: '1.0', minWorkbenchVersion: '0.1.0', url: 'https://example.test/old.zip', size: 3, sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2', signature: { keyId: 'test-key', signature: 'signature' } },
      { version: '1.2.0', hostApiVersion: '1.0', minWorkbenchVersion: '0.1.0', url: 'https://example.test/new.zip', size: 3, sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2', signature: { keyId: 'test-key', signature: 'signature' } },
      { version: '2.0.0', hostApiVersion: '1.0', minWorkbenchVersion: '0.2.0', url: 'https://example.test/future.zip', size: 3, sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2', signature: { keyId: 'test-key', signature: 'signature' } }
    ]
  }]
};

describe('应用目录客户端', () => {
  it('在线读取严格目录并写入有效缓存', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-catalog-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const client = new AppCatalogClient({ catalogUrl: 'https://example.test/catalog.json', repository, request: async () => jsonResponse(catalog) });

    const snapshot = await client.refresh();

    expect(snapshot.fromCache).toBe(false);
    expect(snapshot.catalog).toEqual(catalog);
    expect(repository.loadCatalogSnapshot()).toEqual(snapshot);
    repository.close();
  });

  it('网络失败时返回有效缓存并给出中文警告', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-catalog-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const cached = { catalog, fetchedAt: '2026-08-26T00:00:00.000Z', fromCache: false };
    repository.saveCatalogSnapshot(cached);
    const client = new AppCatalogClient({ catalogUrl: 'https://example.test/catalog.json', repository, request: async () => { throw new Error('网络不可用'); } });

    const snapshot = await client.refresh();

    expect(snapshot.fromCache).toBe(true);
    expect(snapshot.catalog).toEqual(catalog);
    expect(snapshot.warning).toContain('应用目录');
    repository.close();
  });

  it('无效在线目录不会覆盖已有缓存', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-catalog-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const cached = { catalog, fetchedAt: '2026-08-26T00:00:00.000Z', fromCache: false };
    repository.saveCatalogSnapshot(cached);
    const client = new AppCatalogClient({ catalogUrl: 'https://example.test/catalog.json', repository, request: async () => jsonResponse({ schemaVersion: 1, apps: [{ id: 'analysis-center' }] }) });

    const snapshot = await client.refresh();

    expect(snapshot.fromCache).toBe(true);
    expect(snapshot.catalog).toEqual(catalog);
    expect(repository.loadCatalogSnapshot()).toEqual(cached);
    repository.close();
  });

  it('下载 GitHub Release 应用包时跟随重定向', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-catalog-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const payload = new Uint8Array([1, 2, 3]);
    let requestedUrl = '';
    let requestOptions: RequestInit | undefined;
    const client = new AppCatalogClient({
      catalogUrl: 'https://example.test/catalog.json',
      repository,
      request: async (url, options) => {
        requestedUrl = url;
        requestOptions = options;
        return { ok: true, status: 200, text: async () => '', arrayBuffer: async () => payload.buffer };
      }
    });

    try {
      await expect(client.download(catalog.apps[0]!.releases[0]!)).resolves.toEqual(payload);

      expect(requestedUrl).toBe('https://example.test/old.zip');
      expect(requestOptions).toMatchObject({ redirect: 'follow' });
    } finally {
      repository.close();
    }
  });

  it('下载应用包的网络异常包含中文上下文', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-catalog-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const client = new AppCatalogClient({
      catalogUrl: 'https://example.test/catalog.json',
      repository,
      request: async () => { throw new Error('网络不可用'); }
    });

    try {
      await expect(client.download(catalog.apps[0]!.releases[0]!)).rejects.toThrow('下载应用包失败：网络不可用');
    } finally {
      repository.close();
    }
  });

  it('选择满足宿主版本的最高稳定应用版本', () => {
    const app = catalog.apps[0] as AppCatalogItem;
    expect(selectLatestCompatibleAppRelease(app, '0.1.0', '1.0')?.version).toBe('1.2.0');
  });
});

function jsonResponse(value: unknown): AppHttpResponse {
  return { ok: true, status: 200, text: async () => JSON.stringify(value), arrayBuffer: async () => new TextEncoder().encode(JSON.stringify(value)).buffer };
}
