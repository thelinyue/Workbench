import { access, readFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import type { AppManifestV1 } from '../../shared/app-contract';
import { parseAppManifest } from './app-package-validator';

const APP_ID_PATTERN = /^[a-z0-9]+(?:[.-][a-z0-9]+)*$/;

export interface DevelopmentAppOverride {
  appId: string;
  installPath: string;
  manifest: AppManifestV1;
}

export interface DevelopmentAppOverrideOptions {
  isPackaged: boolean;
  environment?: NodeJS.ProcessEnv;
}

/**
 * 解析未打包 Workbench 的单应用本地构建覆盖。
 *
 * 正式应用始终通过已签名 ZIP 安装；此服务只在 Electron 开发运行时读取开发者显式传入的 dist，
 * 因此不会改变安装器、Catalog 或发布包的信任边界。
 */
export async function loadDevelopmentAppOverride(options: DevelopmentAppOverrideOptions): Promise<DevelopmentAppOverride | undefined> {
  if (options.isPackaged) return undefined;

  const environment = options.environment ?? process.env;
  const appId = environment.HEPHAESTUS_DEV_APP_ID;
  const configuredPath = environment.HEPHAESTUS_DEV_APP_DIST;
  if (!appId && !configuredPath) return undefined;
  if (!appId || !configuredPath) throw new Error('本地开发应用配置不完整，需要同时指定应用 ID 和 dist 目录。');
  if (!APP_ID_PATTERN.test(appId)) throw new Error(`本地开发应用 ID 无效：${appId}`);

  const installPath = resolve(configuredPath);
  return { appId, installPath, manifest: await readDevelopmentAppManifest(appId, installPath) };
}

/** 重新读取构建目录中的 manifest，让重载使用当前 renderer、backend 和能力声明。 */
export async function reloadDevelopmentAppOverride(override: DevelopmentAppOverride): Promise<DevelopmentAppOverride> {
  return { ...override, manifest: await readDevelopmentAppManifest(override.appId, override.installPath) };
}

async function readDevelopmentAppManifest(appId: string, installPath: string): Promise<AppManifestV1> {
  const manifestPath = join(installPath, 'manifest.json');
  try {
    await access(manifestPath);
  } catch {
    throw new Error(`本地开发应用目录不存在或缺少 manifest.json：${installPath}`);
  }

  let manifest: AppManifestV1;
  try {
    manifest = parseAppManifest(JSON.parse(await readFile(manifestPath, 'utf8')));
  } catch (error) {
    throw new Error(`本地开发应用 manifest 无效：${error instanceof Error ? error.message : String(error)}`);
  }
  if (manifest.id !== appId) throw new Error(`本地开发应用 manifest 与指定应用 ID 不一致：${manifest.id}`);
  return manifest;
}
