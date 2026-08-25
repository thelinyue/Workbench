import { createRequire } from 'node:module';
import { randomUUID } from 'node:crypto';
import { join } from 'node:path';
import { mkdir, mkdtemp, readFile, rename, rm, writeFile } from 'node:fs/promises';
import extractZip from 'extract-zip';
import { AppRegistryRepository } from '../data/app-registry-repository';
import type { AppCatalogItem, AppCatalogRelease, AppInstallRecord } from '../../shared/app-contract';
import { assertSafeAppArchiveEntry, isCompatibleAppRelease, parseAppManifest, verifyAppReleasePayload } from './app-package-validator';

const require = createRequire(import.meta.url);
const yauzl = require('yauzl') as {
  open(path: string, options: { lazyEntries: boolean; strictFileNames: boolean }, callback: (error: Error | null, zipFile?: ZipFile) => void): void;
};

interface ZipFile {
  readEntry(): void;
  close(): void;
  on(event: 'entry', listener: (entry: ZipEntry) => void): void;
  on(event: 'error', listener: (error: Error) => void): void;
  on(event: 'end', listener: () => void): void;
}

interface ZipEntry {
  fileName: string;
  externalFileAttributes: number;
  versionMadeBy: number;
}

export interface AppPackageInstallerOptions {
  appsRoot: string;
  workbenchVersion: string;
  hostApiVersion: string;
  trustedKeys: Record<string, Parameters<typeof verifyAppReleasePayload>[2][string]>;
  repository: AppRegistryRepository;
}

/**
 * 应用包安装器只在完整校验成功后切换 active 版本。
 * staging 目录和版本目录均位于应用中心专属根目录，失败时不会触碰当前激活版本。
 */
export class AppPackageInstaller {
  public constructor(private readonly options: AppPackageInstallerOptions) {}

  public async installRelease(app: AppCatalogItem, release: AppCatalogRelease, payload: Uint8Array): Promise<AppInstallRecord> {
    if (app.releases.every((item) => item.version !== release.version)) throw new Error(`应用目录中不存在指定版本：${app.id}@${release.version}`);
    if (!isCompatibleAppRelease(release, this.options.workbenchVersion, this.options.hostApiVersion)) throw new Error(`应用版本与当前工作台不兼容：${app.id}@${release.version}`);
    verifyAppReleasePayload(payload, release, this.options.trustedKeys);

    await mkdir(this.options.appsRoot, { recursive: true });
    const stagingRoot = await mkdtemp(join(this.options.appsRoot, `.staging-${app.id}-`));
    const zipPath = join(stagingRoot, 'package.zip');
    const extractedRoot = join(stagingRoot, 'package');
    try {
      await writeFile(zipPath, payload);
      const entries = await inspectZip(zipPath);
      validateEntries(entries);
      await extractZip(zipPath, { dir: extractedRoot });
      const manifest = parseAppManifest(JSON.parse(await readFile(join(extractedRoot, 'manifest.json'), 'utf8')));
      if (manifest.id !== app.id || manifest.version !== release.version || manifest.publisherId !== app.publisherId) {
        throw new Error('应用包 manifest 与目录条目不一致');
      }
      if (manifest.hostApiVersion !== release.hostApiVersion || manifest.minWorkbenchVersion !== release.minWorkbenchVersion) {
        throw new Error('应用包 manifest 的兼容版本与目录不一致');
      }
      const entryNames = new Set(entries.map((entry) => entry.fileName.replaceAll('\\', '/').replace(/\/$/, '')));
      const requiredEntries = [manifest.runtime.rendererEntry, manifest.runtime.icon];
      if ('backendEntry' in manifest.runtime) requiredEntries.push(manifest.runtime.backendEntry);
      for (const entry of requiredEntries) {
        if (!entryNames.has(entry)) throw new Error(`应用包缺少运行时入口：${entry}`);
      }

      const versionDirectory = join(this.options.appsRoot, app.id, release.version);
      const appRoot = join(this.options.appsRoot, app.id);
      await mkdir(appRoot, { recursive: true });
      const backupDirectory = join(appRoot, `.previous-${release.version}-${randomUUID()}`);
      let hasBackup = false;
      try {
        await rename(versionDirectory, backupDirectory);
        hasBackup = true;
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code !== 'ENOENT') throw new Error(`无法暂存旧应用版本：${error instanceof Error ? error.message : String(error)}`);
      }
      try {
        await rename(extractedRoot, versionDirectory);
        const record: AppInstallRecord = {
          id: app.id,
          name: app.name,
          description: app.description,
          publisherId: app.publisherId,
          installedVersion: release.version,
          activeVersion: release.version,
          installPath: versionDirectory,
          state: 'installed'
        };
        this.options.repository.upsert(record);
        if (hasBackup) await rm(backupDirectory, { recursive: true, force: true });
        return record;
      } catch (error) {
        await rm(versionDirectory, { recursive: true, force: true });
        if (hasBackup) await rename(backupDirectory, versionDirectory).catch(() => undefined);
        throw error;
      }
    } catch (error) {
      throw error instanceof Error ? error : new Error(`应用包安装失败：${String(error)}`);
    } finally {
      await rm(stagingRoot, { recursive: true, force: true });
    }
  }
}

function inspectZip(zipPath: string): Promise<ZipEntry[]> {
  return new Promise((resolve, reject) => {
    yauzl.open(zipPath, { lazyEntries: true, strictFileNames: true }, (error, zipFile) => {
      if (error || !zipFile) { reject(new Error(`无法读取应用 ZIP：${error?.message ?? '未知错误'}`)); return; }
      const entries: ZipEntry[] = [];
      zipFile.on('entry', (entry) => { entries.push(entry); zipFile.readEntry(); });
      zipFile.on('error', reject);
      zipFile.on('end', () => resolve(entries));
      zipFile.readEntry();
    });
  });
}

function validateEntries(entries: ZipEntry[]): void {
  const manifestEntries = entries.filter((entry) => entry.fileName === 'manifest.json');
  if (manifestEntries.length !== 1) throw new Error('应用 ZIP 根目录必须且只能包含一个 manifest.json');
  const names = new Set<string>();
  for (const entry of entries) {
    const normalized = entry.fileName.replaceAll('\\', '/');
    assertSafeAppArchiveEntry(normalized.endsWith('/') ? normalized.slice(0, -1) : normalized, '应用 ZIP 条目路径');
    const fileName = normalized.replace(/\/$/, '');
    if (names.has(fileName)) throw new Error(`应用 ZIP 包含重复条目：${entry.fileName}`);
    names.add(fileName);
    const mode = (entry.externalFileAttributes >> 16) & 0xF000;
    if (mode === 0xA000) throw new Error(`应用 ZIP 不允许符号链接：${entry.fileName}`);
  }
}
