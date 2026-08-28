import { generateKeyPairSync, sign } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import {
  assertSafeAppArchiveEntry,
  isCompatibleAppRelease,
  parseAppCatalog,
  parseAppManifest,
  verifyAppReleasePayload,
} from '../../src/main/services/app-package-validator';
import type { AppCatalogRelease, AppManifestV1 } from '../../src/shared/app-contract';

const manifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'analysis-center',
  name: '分析中心',
  description: '诊断包与日志报告',
  publisherId: 'thelinyue',
  version: '1.0.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.0',
  runtime: {
    rendererEntry: 'renderer/index.html',
    backendEntry: 'backend/entry.js',
    icon: 'renderer/icon.png'
  },
  capabilities: ['file.open', 'shell.openPath']
};

function release(overrides: Partial<AppCatalogRelease> = {}): AppCatalogRelease {
  return {
    version: '1.0.0',
    hostApiVersion: '1.0',
    minWorkbenchVersion: '0.1.0',
    url: 'https://example.test/analysis-center-1.0.0.zip',
    size: 3,
    sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2',
    signature: { keyId: 'thelinyue-apps-ed25519-2026', signature: 'placeholder' },
    ...overrides
  };
}

describe('应用包契约校验', () => {
  it('拒绝 manifest 中未知字段和不安全入口路径', () => {
    expect(() => parseAppManifest({ ...manifest, unexpected: true })).toThrow('未知字段');
    expect(() => parseAppManifest({ ...manifest, runtime: { ...manifest.runtime, backendEntry: '../backend.js' } })).toThrow('入口路径');
  });

  it('接受不需要 backend 的纯 Web 应用 manifest', () => {
    const webManifest = { ...manifest, id: 'lvm-uncache-tool', name: 'LVM 缓存清理工具', runtime: { kind: 'web', rendererEntry: 'index.html', icon: 'icon.svg' }, capabilities: ['file.open'] };

    expect(parseAppManifest(webManifest)).toMatchObject({ id: 'lvm-uncache-tool', runtime: { kind: 'web', rendererEntry: 'index.html' } });
  });

  it('保留旧 manifest，并校验独立窗口的安全尺寸和最小尺寸关系', () => {
    expect(parseAppManifest(manifest).window).toBeUndefined();
    expect(parseAppManifest({
      ...manifest,
      window: { defaultSize: { width: 1200, height: 800 }, minSize: { width: 800, height: 560 } }
    })).toMatchObject({ window: { defaultSize: { width: 1200, height: 800 }, minSize: { width: 800, height: 560 } } });
    expect(() => parseAppManifest({
      ...manifest,
      window: { defaultSize: { width: 799, height: 560 }, minSize: { width: 800, height: 560 } }
    })).toThrow('默认宽度不能小于最小宽度');
    expect(() => parseAppManifest({
      ...manifest,
      window: { defaultSize: { width: 1200.5, height: 800 }, minSize: { width: 800, height: 560 } }
    })).toThrow('窗口宽度');
    expect(() => parseAppManifest({
      ...manifest,
      window: { defaultSize: { width: 1200, height: 800 }, minSize: { width: 800, height: 239 } }
    })).toThrow('窗口高度');
  });

  it('解析严格的应用目录并拒绝重复应用或版本', () => {
    const catalog = { schemaVersion: 1, apps: [{ id: 'analysis-center', name: '分析中心', description: '诊断包与日志报告', publisherId: 'thelinyue', releases: [release()] }] };
    expect(parseAppCatalog(catalog).apps[0]?.id).toBe('analysis-center');
    expect(() => parseAppCatalog({ ...catalog, extra: true })).toThrow('未知字段');
    expect(() => parseAppCatalog({ schemaVersion: 1, apps: [...catalog.apps, catalog.apps[0]] })).toThrow('重复应用');
    expect(() => parseAppCatalog({ schemaVersion: 1, apps: [{ ...catalog.apps[0], releases: [release(), release()] }] })).toThrow('重复版本');
  });

  it('只选择满足工作台和宿主 API 版本的稳定版本', () => {
    expect(isCompatibleAppRelease(release({ minWorkbenchVersion: '0.2.0' }), '0.1.0', '1.0')).toBe(false);
    expect(isCompatibleAppRelease(release({ minWorkbenchVersion: '0.1.0' }), '0.1.0', '2.0')).toBe(false);
    expect(isCompatibleAppRelease(release({ version: '1.0.0-beta.1' }), '0.1.0', '1.0')).toBe(false);
    expect(isCompatibleAppRelease(release(), '0.1.0', '1.0')).toBe(true);
  });

  it('校验下载内容的大小、SHA-256 和 Ed25519 签名', () => {
    const payload = Buffer.from('zip');
    const { privateKey, publicKey } = generateKeyPairSync('ed25519');
    const signature = sign(null, payload, privateKey).toString('base64');
    const validRelease = release({
      size: payload.length,
      sha256: '4a70fe9aa6436e02c2dea340fbd1e352e4ef2d8ce6ca52ad25d4b95471fc8bf2',
      signature: { keyId: 'test-key', signature }
    });

    expect(() => verifyAppReleasePayload(payload, validRelease, { 'test-key': publicKey })).not.toThrow();
    expect(() => verifyAppReleasePayload(Buffer.from('bad'), validRelease, { 'test-key': publicKey })).toThrow('SHA-256');
    expect(() => verifyAppReleasePayload(payload, release({ ...validRelease, signature: { ...validRelease.signature, keyId: 'missing' } }), { 'test-key': publicKey })).toThrow('不受信任');
    expect(() => verifyAppReleasePayload(payload, release({ ...validRelease, signature: { ...validRelease.signature, signature: '不是 base64' } }), { 'test-key': publicKey })).toThrow('签名格式');
  });

  it('拒绝应用 ZIP 的绝对路径、路径穿越和反斜杠穿越', () => {
    expect(() => assertSafeAppArchiveEntry('renderer/index.html')).not.toThrow();
    expect(() => assertSafeAppArchiveEntry('/Windows/System32/app.exe')).toThrow('路径');
    expect(() => assertSafeAppArchiveEntry('../backend/entry.js')).toThrow('路径');
    expect(() => assertSafeAppArchiveEntry('renderer\\..\\..\\secret.txt')).toThrow('路径');
  });
});
