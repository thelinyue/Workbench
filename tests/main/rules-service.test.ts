import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { RulesService } from '../../src/main/services/rules-service';

const official = {
  tgz: { version: 'official-1', files: [{ name: 'syslog', category: '系统日志', keywords: [{ term: 'ERROR', result: '系统错误' }] }] },
  zip: { version: 'official-1', files: [] }
};

const RULE_SET_ID = 'analysis-center-rules';

function makeSignedRelease(overrides: Record<string, unknown> = {}) {
  const { privateKey, publicKey } = generateKeyPairSync('ed25519');
  const rulePackage = {
    schemaVersion: 1,
    ruleSetId: RULE_SET_ID,
    version: '2026.08.26',
    tgz: { files: [{ name: 'kern', category: '系统日志', file_patterns: ['^kern(?:\\.log)?$'], keywords: [{ term: 'Kernel panic', result: '内核崩溃' }] }] },
    zip: { files: [{ name: 'zip_dmsg', category: '系统日志', keywords: [{ term: 'Kernel panic', result: '内核崩溃' }] }] },
    ...overrides
  };
  const bytes = Buffer.from(`${JSON.stringify(rulePackage, null, 2)}\n`);
  const catalog = {
    schemaVersion: 1,
    ruleSetId: RULE_SET_ID,
    version: rulePackage.version,
    packageUrl: 'https://example.test/rules/2026.08.26.json',
    packageSize: bytes.byteLength,
    sha256: createHash('sha256').update(bytes).digest('hex'),
    signatureAlgorithm: 'Ed25519',
    keyId: 'test-key',
    signature: sign(null, bytes, privateKey).toString('base64'),
    releaseNotesUrl: 'https://example.test/releases/2026.08.26'
  };
  return { bytes, catalog, publicKey };
}

function makeFetch(catalog: object, bytes: Uint8Array): typeof fetch {
  return (async (input: string | URL | Request) => {
    const url = String(input);
    return url.endsWith('catalog.json')
      ? new Response(JSON.stringify(catalog), { status: 200 })
      : new Response(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength) as ArrayBuffer, { status: 200 });
  }) as typeof fetch;
}

describe('RulesService', () => {
  it('保存用户规则后生成可供分析中心使用的 active 规则快照', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const service = new RulesService({ rootDirectory: root, officialRules: official });

    const result = await service.invoke('rules.saveUserRules', {
      user: { schemaVersion: 1, baseVersion: 'official-1', categories: ['网络'], rules: [{ localId: 'r1', file: 'syslog', category: '网络', status: 'draft', rule: { term: 'timeout', result: '连接超时', regex: false, context_lines: 1, context_direction: 'down', search_direction: 'down' } }] }
    }) as { type: string; data: unknown };

    expect(result.type).toBe('saveSucceeded');
    const active = JSON.parse(await readFile(join(root, 'Active', 'active.json'), 'utf8')) as typeof official;
    expect(active.tgz.files[0]?.keywords).toHaveLength(2);
  });

  it('拒绝无效正则并返回中文校验问题', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const service = new RulesService({ rootDirectory: root, officialRules: official });
    const result = await service.invoke('rules.validateUserRules', { user: { schemaVersion: 1, categories: [], rules: [{ localId: 'r1', file: 'syslog', category: '网络', status: 'draft', rule: { term: '[', result: '无效', regex: true } }] } }) as { type: string; data: { issues: Array<{ severity: string }> } };

    expect(result.type).toBe('validationResult');
    expect(result.data.issues[0]?.severity).toBe('error');
  });

  it('下载并验签官方规则后原子激活，新快照使用下载版本', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });

    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({
      data: { ruleSetId: RULE_SET_ID, currentVersion: 'official-1', source: 'bundled' }
    });
    await expect(service.invoke('rules.updateOfficial', undefined)).resolves.toMatchObject({
      data: { status: 'updated', previousVersion: 'official-1', currentVersion: '2026.08.26' }
    });
    await expect(service.invoke('rules.getActive', undefined)).resolves.toMatchObject({
      data: { tgz: { version: '2026.08.26' }, zip: { version: '2026.08.26' } }
    });
    await expect(readFile(join(root, 'Official', 'versions', '2026.08.26.json'))).resolves.toEqual(release.bytes);
  });

  it('签名校验失败时保留当前官方规则', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    release.catalog.signature = Buffer.alloc(64).toString('base64');
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });

    await expect(service.invoke('rules.updateOfficial', undefined)).rejects.toThrow('签名校验失败');
    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({
      data: { currentVersion: 'official-1', source: 'bundled' }
    });
  });

  it.each([
    ['内容被篡改', (release: ReturnType<typeof makeSignedRelease>) => { release.bytes = Buffer.from(release.bytes); release.bytes[release.bytes.length - 2] ^= 1; }, 'SHA-256'],
    ['大小不一致', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.packageSize += 1; }, '大小'],
    ['哈希不一致', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.sha256 = '0'.repeat(64); }, 'SHA-256'],
    ['未知 key ID', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.keyId = 'unknown-key'; }, '不受信任'],
    ['Catalog 规则集 ID', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.ruleSetId = 'other-rules'; }, '规则目录格式无效'],
    ['Catalog Schema', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.schemaVersion = 2; }, '规则目录格式无效'],
    ['Catalog 未知字段', (release: ReturnType<typeof makeSignedRelease>) => { (release.catalog as Record<string, unknown>).unexpected = true; }, '未知字段'],
    ['Catalog 非 HTTPS 地址', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.packageUrl = 'http://example.test/rules.json'; }, 'HTTPS'],
    ['Catalog 与规则包版本不一致', (release: ReturnType<typeof makeSignedRelease>) => { release.catalog.version = '2026.08.27'; }, '版本与目录不一致']
  ])('拒绝%s且不切换当前版本', async (_label, mutate, expected) => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    mutate(release);
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });

    await expect(service.invoke('rules.updateOfficial', undefined)).rejects.toThrow(expected);
    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({ data: { currentVersion: 'official-1' } });
  });

  it.each([
    ['规则集 ID', { ruleSetId: 'other-rules' }, '规则包格式无效'],
    ['Schema', { schemaVersion: 2 }, '规则包格式无效']
  ])('拒绝签名有效但%s无效的规则包', async (_label, overrides, expected) => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease(overrides);
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });

    await expect(service.invoke('rules.updateOfficial', undefined)).rejects.toThrow(expected);
    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({ data: { currentVersion: 'official-1' } });
  });

  it('重新启动服务后恢复已激活且校验有效的下载规则', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    const options = {
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    };
    await new RulesService(options).invoke('rules.updateOfficial', undefined);

    const restarted = new RulesService({ ...options, fetchImpl: (() => { throw new Error('离线'); }) as typeof fetch });
    await expect(restarted.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({
      data: { currentVersion: '2026.08.26', source: 'downloaded' }
    });
  });

  it('当前元数据损坏时回退内置规则且保留最后下载的版本文件', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    const options = {
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    };
    await new RulesService(options).invoke('rules.updateOfficial', undefined);
    await writeFile(join(root, 'Official', 'current.json'), '{invalid', 'utf8');

    const restarted = new RulesService(options);
    await expect(restarted.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({
      data: { currentVersion: 'official-1', source: 'bundled' }
    });
    await expect(readFile(join(root, 'Official', 'versions', '2026.08.26.json'))).resolves.toEqual(release.bytes);
  });

  it('更新下载期间旧快照保持不变，激活后新读取只得到完整新版', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    let allowPackageDownload!: () => void;
    const packageDownloadAllowed = new Promise<void>((resolve) => { allowPackageDownload = resolve; });
    let markPackageRequested!: () => void;
    const packageRequested = new Promise<void>((resolve) => { markPackageRequested = resolve; });
    const fetchImpl = (async (input: string | URL | Request) => {
      if (String(input).endsWith('catalog.json')) return new Response(JSON.stringify(release.catalog), { status: 200 });
      markPackageRequested();
      await packageDownloadAllowed;
      return new Response(release.bytes, { status: 200 });
    }) as typeof fetch;
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl
    });

    const oldSnapshot = (await service.invoke('rules.getActive', undefined)).data as typeof official;
    const updating = service.invoke('rules.updateOfficial', undefined);
    await packageRequested;
    await expect(service.invoke('rules.getActive', undefined)).resolves.toMatchObject({ data: { tgz: { version: 'official-1' } } });
    allowPackageDownload();
    await updating;
    await expect(service.invoke('rules.getActive', undefined)).resolves.toMatchObject({ data: { tgz: { version: '2026.08.26' } } });
    expect(oldSnapshot.tgz.version).toBe('official-1');
    expect(oldSnapshot.tgz.files[0]?.keywords).toHaveLength(1);
  });

  it('更新官方规则后保留本地增量，但不激活 conflict 和 rejected 项', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease();
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });
    await service.invoke('rules.saveUserRules', { user: {
      schemaVersion: 1,
      baseVersion: 'official-1',
      categories: ['系统日志'],
      rules: [
        { localId: 'draft', file: 'kern', category: '系统日志', status: 'draft', rule: { term: 'timeout', result: '本地超时' } },
        { localId: 'conflict', file: 'kern', category: '系统日志', status: 'conflict', rule: { term: 'conflict-only', result: '冲突项' } },
        { localId: 'rejected', file: 'kern', category: '系统日志', status: 'rejected', rule: { term: 'rejected-only', result: '拒绝项' } }
      ]
    } });

    await service.invoke('rules.updateOfficial', undefined);
    const active = (await service.invoke('rules.getActive', undefined)).data as typeof official;
    const terms = active.tgz.files.find((file) => file.name === 'kern')?.keywords.map((keyword) => keyword.term);
    expect(terms).toEqual(['Kernel panic', 'timeout']);
  });

  it.each([
    ['无效关键词正则', { tgz: { files: [{ name: 'kern', category: '系统日志', keywords: [{ term: '[', result: '无效', regex: true }] }] } }, '关键词正则无效'],
    ['无效文件匹配正则', { tgz: { files: [{ name: 'kern', category: '系统日志', file_patterns: ['['], keywords: [] }] } }, '文件匹配正则无效'],
    ['重复关键词', { tgz: { files: [{ name: 'kern', category: '系统日志', keywords: [{ term: 'panic', result: '一' }, { term: 'panic', result: '二' }] }] } }, '重复规则'],
    ['未知字段', { unexpected: true }, '未知']
  ])('拒绝签名有效但包含%s的规则包', async (_label, overrides, expected) => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const release = makeSignedRelease(overrides);
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      trustedKeys: { 'test-key': release.publicKey },
      fetchImpl: makeFetch(release.catalog, release.bytes)
    });

    await expect(service.invoke('rules.updateOfficial', undefined)).rejects.toThrow(expected);
    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({ data: { currentVersion: 'official-1' } });
  });

  it('网络失败时保留当前规则并返回中文错误', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const service = new RulesService({
      rootDirectory: root,
      officialRules: official,
      catalogUrl: 'https://example.test/catalog.json',
      fetchImpl: (async () => { throw new Error('连接超时'); }) as typeof fetch
    });

    await expect(service.invoke('rules.updateOfficial', undefined)).rejects.toThrow('规则目录下载失败：连接超时');
    await expect(service.invoke('rules.getUpdateState', undefined)).resolves.toMatchObject({ data: { currentVersion: 'official-1', source: 'bundled' } });
  });
});
