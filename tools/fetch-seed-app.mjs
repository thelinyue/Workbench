import { createHash, createPublicKey, verify } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('..', import.meta.url)));
const version = process.env.HEPHAESTUS_SEED_APP_VERSION ?? '1.0.0';
const baseUrl = (process.env.HEPHAESTUS_APPS_RELEASE_BASE_URL ?? 'https://github.com/thelinyue/Workbench-Apps/releases/download').replace(/\/$/, '');
const outputDirectory = join(root, 'build', 'seed-app');
const trustedKeysPath = join(root, 'src', 'main', 'config', 'app-trusted-keys.json');

export function validateSeedRelease(release) {
  if (!release || typeof release !== 'object') throw new Error('种子应用 release.json 格式无效。');
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(release.version)) throw new Error('种子应用版本号无效。');
  if (!/^\d+\.\d+$/.test(release.hostApiVersion) || !/^\d+\.\d+\.\d+/.test(release.minWorkbenchVersion)) throw new Error('种子应用宿主兼容版本无效。');
  const url = new URL(release.url);
  if (url.protocol !== 'https:' || url.username || url.password || url.hash) throw new Error('种子应用下载地址必须是安全的 HTTPS 地址。');
  if (!Number.isInteger(release.size) || release.size <= 0 || release.size > 200 * 1024 * 1024) throw new Error('种子应用大小无效。');
  if (!/^[0-9a-f]{64}$/i.test(release.sha256)) throw new Error('种子应用 SHA-256 无效。');
  if (!release.signature?.keyId || typeof release.signature.signature !== 'string') throw new Error('种子应用缺少发布签名。');
  return release;
}

export function verifySeedPayload(payload, release, trustedKeys) {
  validateSeedRelease(release);
  if (payload.byteLength !== release.size) throw new Error(`种子应用大小校验失败：预期 ${release.size} 字节，实际 ${payload.byteLength} 字节。`);
  const digest = createHash('sha256').update(payload).digest('hex');
  if (digest.toLowerCase() !== release.sha256.toLowerCase()) throw new Error('种子应用 SHA-256 校验失败，拒绝继续打包。');
  const publicKey = trustedKeys[release.signature.keyId];
  if (!publicKey) throw new Error(`种子应用签名密钥不受信任：${release.signature.keyId}`);
  let signature;
  try { signature = Buffer.from(release.signature.signature, 'base64'); } catch { throw new Error('种子应用签名格式无效。'); }
  if (signature.byteLength !== 64 || !verify(null, payload, createPublicKey(publicKey), signature)) throw new Error('种子应用签名校验失败，拒绝继续打包。');
}

export async function fetchSeedApp(options = {}) {
  const { fetchImpl = fetch, outputDir = outputDirectory, trustedKeys: configuredKeys } = options;
  const trustedKeys = configuredKeys ?? await loadTrustedKeys();
  const releaseUrl = `${baseUrl}/analysis-center-v${version}/release.json`;
  const releaseResponse = await fetchImpl(releaseUrl);
  if (!releaseResponse.ok) throw new Error(`无法下载种子应用 release.json：HTTP ${releaseResponse.status} ${releaseResponse.statusText ?? ''}`.trim());
  const release = validateSeedRelease(await releaseResponse.json());
  const packageResponse = await fetchImpl(release.url);
  if (!packageResponse.ok) throw new Error(`无法下载种子应用 ZIP：HTTP ${packageResponse.status} ${packageResponse.statusText ?? ''}`.trim());
  const payload = Buffer.from(await packageResponse.arrayBuffer());
  verifySeedPayload(payload, release, trustedKeys);
  await mkdir(outputDir, { recursive: true });
  await writeFile(join(outputDir, 'analysis-center.zip'), payload);
  await writeFile(join(outputDir, 'release.json'), `${JSON.stringify({ appId: 'analysis-center', ...release }, null, 2)}\n`, 'utf8');
  console.log(`已下载并校验分析中心种子包：${release.version}，SHA-256 ${release.sha256}`);
  return release;
}

async function loadTrustedKeys() {
  return JSON.parse(await readFile(trustedKeysPath, 'utf8'));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await fetchSeedApp();
