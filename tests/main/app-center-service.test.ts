import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { AppRegistryRepository } from '../../src/main/data/app-registry-repository';
import { AppCenterService } from '../../src/main/services/app-center-service';
import type { AppCatalogDocumentV1, AppCatalogRelease, AppInstallRecord } from '../../src/shared/app-contract';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

describe('应用中心服务', () => {
  it('把目录、已安装版本和兼容性合并为应用状态', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-center-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    repository.upsert({ id: 'analysis-center', name: '分析中心', description: '旧描述', publisherId: 'thelinyue', installedVersion: '1.0.0', activeVersion: '1.0.0', enabled: true, state: 'installed' });
    const catalog = makeCatalog();
    const service = new AppCenterService({ repository, catalog: new FakeCatalogClient(catalog), installer: new FakeInstaller(), workbenchVersion: '0.1.0', hostApiVersion: '1.0' });

    const apps = await service.refresh();

    expect(apps).toEqual([expect.objectContaining({ id: 'analysis-center', name: '分析中心', installedVersion: '1.0.0', availableVersion: '1.2.0', state: 'update-available' })]);
    repository.close();
  });

  it('未安装应用默认停用，已停用应用始终映射为 stopped 并保留错误信息', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-center-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    repository.upsert({ id: 'analysis-center', name: '分析中心', description: '旧描述', publisherId: 'thelinyue', installedVersion: '1.0.0', activeVersion: '1.0.0', enabled: false, state: 'broken', errorMessage: '启动失败' });
    const service = new AppCenterService({
      repository,
      catalog: new FakeCatalogClient(makeCatalog()),
      installer: new FakeInstaller(),
      workbenchVersion: '0.1.0',
      hostApiVersion: '1.0',
      builtInAppIds: ['analysis-center', 'terminal'],
      runtimeState: () => 'running'
    });

    const apps = await service.refresh();

    expect(apps).toEqual([
      expect.objectContaining({ id: 'analysis-center', enabled: false, runtimeState: 'stopped', errorMessage: '启动失败', builtIn: true }),
    ]);
    repository.close();
  });

  it('安装目录中最高兼容版本并返回已激活记录', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-center-'));
    directories.push(root);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const catalog = makeCatalog();
    const installer = new FakeInstaller();
    const service = new AppCenterService({ repository, catalog: new FakeCatalogClient(catalog), installer, workbenchVersion: '0.1.0', hostApiVersion: '1.0' });

    const installed = await service.install('analysis-center');

    expect(installed).toMatchObject({ id: 'analysis-center', activeVersion: '1.2.0', state: 'installed' });
    expect(installer.lastVersion).toBe('1.2.0');
    repository.close();
  });
});

function makeCatalog(): AppCatalogDocumentV1 {
  const makeRelease = (version: string, minWorkbenchVersion = '0.1.0'): AppCatalogRelease => ({ version, hostApiVersion: '1.0', minWorkbenchVersion, url: `https://example.test/${version}.zip`, size: 1, sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2', signature: { keyId: 'test-key', signature: 'signature' } });
  return { schemaVersion: 1, apps: [{ id: 'analysis-center', name: '分析中心', description: '诊断包与日志报告', publisherId: 'thelinyue', releases: [makeRelease('1.0.0'), makeRelease('1.2.0'), makeRelease('2.0.0', '0.2.0')] }] };
}

class FakeCatalogClient {
  public constructor(private readonly catalog: AppCatalogDocumentV1) {}
  public async refresh() { return { catalog: this.catalog, fetchedAt: '2026-08-26T00:00:00.000Z', fromCache: false }; }
  public async download(release: AppCatalogRelease) { return new Uint8Array([release.version.length]); }
}

class FakeInstaller {
  public lastVersion = '';
  public async installRelease(app: AppCatalogDocumentV1['apps'][number], release: AppCatalogRelease, _payload: Uint8Array): Promise<AppInstallRecord> {
    this.lastVersion = release.version;
    return { id: app.id, name: app.name, description: app.description, publisherId: app.publisherId, installedVersion: release.version, activeVersion: release.version, enabled: true, state: 'installed' };
  }
}
