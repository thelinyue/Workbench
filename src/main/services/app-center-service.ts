import { AppRegistryRepository } from '../data/app-registry-repository';
import type { AppCatalogItem, AppCatalogRelease, AppCatalogSnapshot, AppInstallRecord, AppRuntimeState } from '../../shared/app-contract';
import { compareAppVersions } from './app-package-validator';
import { selectLatestCompatibleAppRelease } from './app-catalog-client';

interface AppCatalogSource {
  refresh(): Promise<AppCatalogSnapshot>;
  download(release: AppCatalogRelease): Promise<Uint8Array>;
}

interface AppInstaller {
  installRelease(app: AppCatalogItem, release: AppCatalogRelease, payload: Uint8Array): Promise<AppInstallRecord>;
}

export interface AppCenterItem extends AppInstallRecord {
  builtIn: boolean;
  runtimeState: AppRuntimeState;
}

export interface AppCenterServiceOptions {
  repository: AppRegistryRepository;
  catalog: AppCatalogSource;
  installer: AppInstaller;
  workbenchVersion: string;
  hostApiVersion: string;
  runtimeState?: (appId: string) => AppRuntimeState;
  builtInAppIds?: ReadonlySet<string> | readonly string[];
}

/**
 * 应用中心编排目录、安装状态和版本更新。
 * registry 是本地状态的唯一来源，Catalog 只提供可用版本，不直接覆盖已安装应用。
 */
export class AppCenterService {
  public constructor(private readonly options: AppCenterServiceOptions) {}

  public async refresh(): Promise<AppCenterItem[]> {
    const snapshot = await this.options.catalog.refresh();
    return this.merge(snapshot.catalog.apps);
  }

  public list(): AppCenterItem[] {
    const cached = this.options.repository.loadCatalogSnapshot();
    return this.merge(cached?.catalog.apps ?? []);
  }

  public getItem(appId: string): AppCenterItem | undefined {
    const record = this.options.repository.get(appId);
    return record ? this.toCenterItem(record) : undefined;
  }

  public async install(appId: string, version?: string): Promise<AppCenterItem> {
    const catalog = this.options.repository.loadCatalogSnapshot()?.catalog ?? (await this.options.catalog.refresh()).catalog;
    const app = catalog.apps.find((item) => item.id === appId);
    if (!app) throw new Error(`应用目录中不存在：${appId}`);
    const release = version ? app.releases.find((item) => item.version === version) : selectLatestCompatibleAppRelease(app, this.options.workbenchVersion, this.options.hostApiVersion);
    if (!release) throw new Error(`没有找到与当前工作台兼容的应用版本：${appId}`);
    const payload = await this.options.catalog.download(release);
    const current = this.options.repository.get(appId);
    const installed = await this.options.installer.installRelease(app, release, payload);
    const record = { ...installed, enabled: current?.enabled ?? true };
    this.options.repository.upsert(record);
    return this.toCenterItem(record);
  }

  private merge(catalogApps: AppCatalogItem[]): AppCenterItem[] {
    const installed = new Map(this.options.repository.list().map((item) => [item.id, item]));
    const result = catalogApps.map((app) => {
      const current = installed.get(app.id);
      const latest = selectLatestCompatibleAppRelease(app, this.options.workbenchVersion, this.options.hostApiVersion);
      const availableVersion = latest?.version;
      if (!current) return this.toCenterItem({ id: app.id, name: app.name, description: app.description, publisherId: app.publisherId, availableVersion, enabled: false, state: latest ? 'not-installed' : 'incompatible' });
      const state = !latest ? 'incompatible' : current.state === 'broken' && (!current.installedVersion || latest.version === current.installedVersion) ? 'broken' : current.installedVersion && compareAppVersions(latest.version, current.installedVersion) > 0 ? 'update-available' : 'installed';
      return this.toCenterItem({ ...current, name: app.name, description: app.description, publisherId: app.publisherId, availableVersion, state });
    });
    const catalogIds = new Set(catalogApps.map((item) => item.id));
    return [...result, ...this.options.repository.list().filter((item) => !catalogIds.has(item.id)).map((item) => this.toCenterItem(item))];
  }

  private toCenterItem(record: AppInstallRecord): AppCenterItem {
    return {
      ...record,
      builtIn: this.isBuiltIn(record.id),
      // disabled 应用即使曾经 failed，也必须向应用中心显示 stopped；errorMessage 仍保留供诊断。
      runtimeState: record.enabled ? (this.options.runtimeState?.(record.id) ?? 'stopped') : 'stopped'
    };
  }

  private isBuiltIn(appId: string): boolean {
    const configured = this.options.builtInAppIds;
    if (!configured) return false;
    return Array.isArray(configured)
      ? (configured as readonly string[]).includes(appId)
      : (configured as ReadonlySet<string>).has(appId);
  }
}
