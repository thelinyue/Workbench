import { AppRegistryRepository } from '../data/app-registry-repository';
import type { AppCatalogItem, AppCatalogRelease, AppCatalogSnapshot, AppInstallRecord } from '../../shared/app-contract';
import { compareAppVersions } from './app-package-validator';
import { selectLatestCompatibleAppRelease } from './app-catalog-client';

interface AppCatalogSource {
  refresh(): Promise<AppCatalogSnapshot>;
  download(release: AppCatalogRelease): Promise<Uint8Array>;
}

interface AppInstaller {
  installRelease(app: AppCatalogItem, release: AppCatalogRelease, payload: Uint8Array): Promise<AppInstallRecord>;
}

export interface AppCenterServiceOptions {
  repository: AppRegistryRepository;
  catalog: AppCatalogSource;
  installer: AppInstaller;
  workbenchVersion: string;
  hostApiVersion: string;
}

/**
 * 应用中心编排目录、安装状态和版本更新。
 * registry 是本地状态的唯一来源，Catalog 只提供可用版本，不直接覆盖已安装应用。
 */
export class AppCenterService {
  public constructor(private readonly options: AppCenterServiceOptions) {}

  public async refresh(): Promise<AppInstallRecord[]> {
    const snapshot = await this.options.catalog.refresh();
    return this.merge(snapshot.catalog.apps);
  }

  public list(): AppInstallRecord[] {
    const cached = this.options.repository.loadCatalogSnapshot();
    return this.merge(cached?.catalog.apps ?? []);
  }

  public async install(appId: string, version?: string): Promise<AppInstallRecord> {
    const catalog = this.options.repository.loadCatalogSnapshot()?.catalog ?? (await this.options.catalog.refresh()).catalog;
    const app = catalog.apps.find((item) => item.id === appId);
    if (!app) throw new Error(`应用目录中不存在：${appId}`);
    const release = version ? app.releases.find((item) => item.version === version) : selectLatestCompatibleAppRelease(app, this.options.workbenchVersion, this.options.hostApiVersion);
    if (!release) throw new Error(`没有找到与当前工作台兼容的应用版本：${appId}`);
    const payload = await this.options.catalog.download(release);
    const installed = await this.options.installer.installRelease(app, release, payload);
    this.options.repository.upsert(installed);
    return installed;
  }

  private merge(catalogApps: AppCatalogItem[]): AppInstallRecord[] {
    const installed = new Map(this.options.repository.list().map((item) => [item.id, item]));
    const result = catalogApps.map((app) => {
      const current = installed.get(app.id);
      const latest = selectLatestCompatibleAppRelease(app, this.options.workbenchVersion, this.options.hostApiVersion);
      const availableVersion = latest?.version;
      if (!current) return { id: app.id, name: app.name, description: app.description, publisherId: app.publisherId, availableVersion, state: latest ? 'not-installed' : 'incompatible' } satisfies AppInstallRecord;
      const state = !latest ? 'incompatible' : current.state === 'broken' && (!current.installedVersion || latest.version === current.installedVersion) ? 'broken' : current.installedVersion && compareAppVersions(latest.version, current.installedVersion) > 0 ? 'update-available' : 'installed';
      return { ...current, name: app.name, description: app.description, publisherId: app.publisherId, availableVersion, state } satisfies AppInstallRecord;
    });
    const catalogIds = new Set(catalogApps.map((item) => item.id));
    return [...result, ...this.options.repository.list().filter((item) => !catalogIds.has(item.id))];
  }
}
