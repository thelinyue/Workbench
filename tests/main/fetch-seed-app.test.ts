import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { fetchSeedApp } from '../../tools/fetch-seed-app.mjs';

describe('fetchSeedApp', () => {
  it('下载后校验两个核心应用的大小、哈希和 Ed25519 签名，并写入独立种子路径', async () => {
    const { privateKey, publicKey } = generateKeyPairSync('ed25519');
    const payload = Buffer.from('seed-app');
    const requestedReleaseUrls: string[] = [];
    const makeRelease = (appId: string) => ({ version: '1.0.0', hostApiVersion: '1.0', minWorkbenchVersion: '0.1.0', url: `https://example.com/${appId}.zip`, size: payload.length, sha256: createHash('sha256').update(payload).digest('hex'), signature: { keyId: 'test-key', signature: sign(null, payload, privateKey).toString('base64') } });
    const fetchImpl = async (input: string | URL | Request) => {
      const url = String(input);
      if (!url.endsWith('release.json')) return new Response(new Uint8Array(payload), { status: 200 });
      requestedReleaseUrls.push(url);
      return new Response(JSON.stringify(makeRelease(url.includes('/terminal-v') ? 'terminal' : 'analysis-center')), { status: 200 });
    };
    const outputDir = await mkdtemp(join(tmpdir(), 'workbench-seed-'));

    const trustedKey = publicKey.export({ type: 'spki', format: 'pem' }).toString();
    await fetchSeedApp({ fetchImpl, outputDir, trustedKeys: { 'test-key': trustedKey } });

    expect(await readFile(join(outputDir, 'analysis-center', 'analysis-center.zip'), 'utf8')).toBe('seed-app');
    expect(JSON.parse(await readFile(join(outputDir, 'analysis-center', 'release.json'), 'utf8'))).toMatchObject({ appId: 'analysis-center', version: '1.0.0' });
    expect(await readFile(join(outputDir, 'terminal', 'terminal.zip'), 'utf8')).toBe('seed-app');
    expect(JSON.parse(await readFile(join(outputDir, 'terminal', 'release.json'), 'utf8'))).toMatchObject({ appId: 'terminal', version: '1.0.0' });
    expect(requestedReleaseUrls).toContain('https://github.com/thelinyue/Workbench-Apps/releases/download/workbench-apps/analysis-center-v2.0.7.release.json');
    expect(requestedReleaseUrls).toContain('https://github.com/thelinyue/Workbench-Apps/releases/download/workbench-apps/terminal-v2.0.0.release.json');
  });
});
