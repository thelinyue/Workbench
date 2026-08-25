import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { createWriteStream } from 'node:fs';
import { access, mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import yazl from 'yazl';
import { afterEach, describe, expect, it } from 'vitest';
import { AppRegistryRepository } from '../../src/main/data/app-registry-repository';
import { AppPackageInstaller } from '../../src/main/services/app-package-installer';
import type { AppCatalogItem, AppCatalogRelease } from '../../src/shared/app-contract';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

describe('应用包安装器', () => {
  it('安装纯 Web 应用时不要求 backend 入口', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-installer-'));
    directories.push(root);
    const { bytes, release, publicKey } = await createPackage('lvm-uncache-tool', '1.0.0', [], true);
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const installer = new AppPackageInstaller({ appsRoot: join(root, 'apps'), workbenchVersion: '0.1.0', hostApiVersion: '1.0', trustedKeys: { 'test-key': publicKey }, repository });
    const app: AppCatalogItem = { id: 'lvm-uncache-tool', name: 'LVM 缓存清理工具', description: '清理 LVM 缓存配置', publisherId: 'thelinyue', releases: [release] };

    try {
      await expect(installer.installRelease(app, release, bytes)).resolves.toMatchObject({ id: 'lvm-uncache-tool', state: 'installed' });
    } finally {
      repository.close();
    }
  });

  it('校验签名后把应用安装到版本目录并激活', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-installer-'));
    directories.push(root);
    const { bytes, release, publicKey } = await createPackage('analysis-center', '1.0.0');
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const installer = new AppPackageInstaller({
      appsRoot: join(root, 'apps'),
      workbenchVersion: '0.1.0',
      hostApiVersion: '1.0',
      trustedKeys: { 'test-key': publicKey },
      repository
    });
    const app: AppCatalogItem = { id: 'analysis-center', name: '分析中心', description: '诊断包与日志报告', publisherId: 'thelinyue', releases: [release] };

    const installed = await installer.installRelease(app, release, bytes);

    expect(installed).toMatchObject({ id: 'analysis-center', installedVersion: '1.0.0', activeVersion: '1.0.0', state: 'installed' });
    await expect(access(join(root, 'apps', 'analysis-center', '1.0.0', 'manifest.json'))).resolves.toBeUndefined();
    await expect(readFile(join(root, 'apps', 'analysis-center', '1.0.0', 'renderer', 'index.html'), 'utf8')).resolves.toBe('<main>分析中心</main>');
    repository.close();
  });

  it('校验失败时不改变当前激活版本', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-app-installer-'));
    directories.push(root);
    const first = await createPackage('analysis-center', '1.0.0');
    const second = await createPackage('analysis-center', '1.1.0');
    const repository = new AppRegistryRepository(join(root, 'apps.db'));
    const installer = new AppPackageInstaller({ appsRoot: join(root, 'apps'), workbenchVersion: '0.1.0', hostApiVersion: '1.0', trustedKeys: { 'test-key': first.publicKey, 'second-key': second.publicKey }, repository });
    const app: AppCatalogItem = { id: 'analysis-center', name: '分析中心', description: '诊断包与日志报告', publisherId: 'thelinyue', releases: [first.release, second.release] };
    await installer.installRelease(app, first.release, first.bytes);

    await expect(installer.installRelease(app, second.release, Buffer.alloc(second.bytes.length, 0))).rejects.toThrow('SHA-256');
    expect(repository.get('analysis-center')).toMatchObject({ activeVersion: '1.0.0', state: 'installed' });
    repository.close();
  });
});

async function createPackage(id: string, version: string, extra: Array<{ name: string; content: string }> = [], web = false) {
  const manifest = {
    schemaVersion: 1,
    id,
    name: '分析中心',
    description: '诊断包与日志报告',
    publisherId: 'thelinyue',
    version,
    hostApiVersion: '1.0',
    minWorkbenchVersion: '0.1.0',
    runtime: web ? { kind: 'web', rendererEntry: 'renderer/index.html', icon: 'renderer/icon.png' } : { rendererEntry: 'renderer/index.html', backendEntry: 'backend/entry.js', icon: 'renderer/icon.png' },
    capabilities: ['file.open']
  };
  const root = await mkdtemp(join(tmpdir(), 'workbench-app-package-'));
  directories.push(root);
  const zipPath = join(root, `${id}-${version}.zip`);
  await new Promise<void>((resolve, reject) => {
    const zip = new yazl.ZipFile();
    zip.outputStream.pipe(createWriteStream(zipPath)).on('close', resolve).on('error', reject);
    zip.addBuffer(Buffer.from(JSON.stringify(manifest)), 'manifest.json');
    zip.addBuffer(Buffer.from('<main>分析中心</main>'), 'renderer/index.html');
    zip.addBuffer(Buffer.from(''), 'renderer/icon.png');
    if (!web) zip.addBuffer(Buffer.from('module.exports = {};'), 'backend/entry.js');
    for (const item of extra) zip.addBuffer(Buffer.from(item.content), item.name);
    zip.end();
  });
  const bytes = await readFile(zipPath);
  const { privateKey, publicKey } = generateKeyPairSync('ed25519');
  const release: AppCatalogRelease = {
    version,
    hostApiVersion: '1.0',
    minWorkbenchVersion: '0.1.0',
    url: `https://example.test/${id}-${version}.zip`,
    size: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex'),
    signature: { keyId: 'test-key', signature: sign(null, bytes, privateKey).toString('base64') }
  };
  return { bytes, release, publicKey };
}
