import { AppRegistryRepository } from '../data/app-registry-repository';
import type { AppCatalogDocumentV1, AppCatalogItem, AppCatalogRelease, AppCatalogSnapshot } from '../../shared/app-contract';
import { compareAppVersions, isCompatibleAppRelease, parseAppCatalog } from './app-package-validator';

export interface AppHttpResponse {
  ok: boolean;
  status: number;
  text(): Promise<string>;
  arrayBuffer(): Promise<ArrayBuffer>;
}

export type AppHttpRequest = (url: string, init?: RequestInit) => Promise<AppHttpResponse>;

export interface AppCatalogClientOptions {
  catalogUrl: string;
  repository: AppRegistryRepository;
  request: AppHttpRequest;
}

/**
 * 应用目录客户端只缓存通过严格校验的目录。
 * 在线失败时保留最近一次有效目录，使离线用户仍可启动已安装应用。
 */
export class AppCatalogClient {
  private readonly request: AppHttpRequest;

  public constructor(private readonly options: AppCatalogClientOptions) {
    let url: URL;
    try { url = new URL(options.catalogUrl); } catch (error) { throw new Error(`应用目录地址无效：${error instanceof Error ? error.message : String(error)}`); }
    if (url.protocol !== 'https:') throw new Error('应用目录地址必须使用 HTTPS');
    this.request = options.request;
  }

  public async refresh(): Promise<AppCatalogSnapshot> {
    try {
      const response = await this.request(this.options.catalogUrl, { redirect: 'error' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const catalog = parseAppCatalog(JSON.parse(await response.text())) as AppCatalogDocumentV1;
      const snapshot: AppCatalogSnapshot = { catalog, fetchedAt: new Date().toISOString(), fromCache: false };
      this.options.repository.saveCatalogSnapshot(snapshot);
      return snapshot;
    } catch (error) {
      const cached = this.options.repository.loadCatalogSnapshot();
      if (cached) {
        return { ...cached, fromCache: true, warning: `应用目录刷新失败，已使用缓存：${describeError(error)}` };
      }
      throw new Error(`无法加载应用目录：${describeError(error)}`);
    }
  }

  public async download(release: AppCatalogRelease): Promise<Uint8Array> {
    try {
      // GitHub Releases 会重定向到临时资源地址；内容仍由后续 SHA-256 与 Ed25519 校验保护。
      const response = await this.request(release.url, { redirect: 'follow' });
      if (!response.ok) throw new Error(`HTTP 状态码：${response.status}`);
      return new Uint8Array(await response.arrayBuffer());
    } catch (error) {
      throw new Error(`下载应用包失败：${describeError(error)}`);
    }
  }
}

export function selectLatestCompatibleAppRelease(app: AppCatalogItem, workbenchVersion: string, hostApiVersion: string): AppCatalogRelease | undefined {
  return app.releases
    .filter((release) => isCompatibleAppRelease(release, workbenchVersion, hostApiVersion))
    .sort((left, right) => compareAppVersions(right.version, left.version))[0];
}

function describeError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  if (message === 'fetch failed' || message === 'Failed to fetch') return '网络请求失败，请检查网络、代理或防火墙后重试。';
  return message;
}
