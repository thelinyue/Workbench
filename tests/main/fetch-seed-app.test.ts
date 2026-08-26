import { generateKeyPairSync, sign } from 'node:crypto';
import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { fetchSeedApp } from '../../tools/fetch-seed-app.mjs';

describe('fetchSeedApp', () => {
  it('下载后校验大小、哈希和 Ed25519 签名，并写入固定种子路径', async () => {
    const { privateKey, publicKey } = generateKeyPairSync('ed25519');
    const payload = Buffer.from('seed-app');
    const release = { version: '1.0.0', hostApiVersion: '1.0', minWorkbenchVersion: '0.1.0', url: 'https://example.com/analysis-center.zip', size: payload.length, sha256: (await import('node:crypto')).createHash('sha256').update(payload).digest('hex'), signature: { keyId: 'test-key', signature: sign(null, payload, privateKey).toString('base64') } };
    const fetchImpl = async (input: string | URL | Request) => String(input).endsWith('release.json') ? new Response(JSON.stringify(release), { status: 200 }) : new Response(new Uint8Array(payload), { status: 200 });
    const outputDir = await mkdtemp(join(tmpdir(), 'workbench-seed-'));

    const trustedKey = publicKey.export({ type: 'spki', format: 'pem' }).toString();
    await fetchSeedApp({ fetchImpl, outputDir, trustedKeys: { 'test-key': trustedKey } });

    expect(await readFile(join(outputDir, 'analysis-center.zip'), 'utf8')).toBe('seed-app');
    expect(JSON.parse(await readFile(join(outputDir, 'release.json'), 'utf8'))).toMatchObject({ appId: 'analysis-center', version: '1.0.0' });
  });
});
