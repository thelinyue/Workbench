import { createHash, verify, type KeyObject } from 'node:crypto';
import { isAbsolute } from 'node:path';
import { z } from 'zod';
import type { AppCatalogDocumentV1, AppCatalogRelease, AppManifestV1 } from '../../shared/app-contract';

const identifierPattern = /^[a-z0-9]+(?:[.-][a-z0-9]+)*$/;
const semverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?$/;

const identifierSchema = z.string().regex(identifierPattern, '标识符格式无效');
const semverSchema = z.string().regex(semverPattern, '版本号必须符合 SemVer');
const hostApiVersionSchema = z.string().regex(/^\d+\.\d+$/, '宿主 API 版本必须为 major.minor');
const relativeEntrySchema = z.string().min(1, '入口路径不能为空');

const appRuntimeWebSchema = z.object({
  kind: z.literal('web'),
  rendererEntry: relativeEntrySchema,
  icon: relativeEntrySchema
}).strict();

const appRuntimeBackendSchema = z.object({
  kind: z.literal('backend').optional(),
  rendererEntry: relativeEntrySchema,
  backendEntry: relativeEntrySchema,
  icon: relativeEntrySchema
}).strict();

const appManifestSchema = z.object({
  schemaVersion: z.literal(1),
  id: identifierSchema,
  name: z.string().trim().min(1, '应用名称不能为空'),
  description: z.string().trim().min(1, '应用描述不能为空'),
  publisherId: identifierSchema,
  version: semverSchema,
  hostApiVersion: hostApiVersionSchema,
  minWorkbenchVersion: semverSchema,
  runtime: z.union([appRuntimeWebSchema, appRuntimeBackendSchema]),
  capabilities: z.array(z.string().regex(/^[a-z][a-z0-9]*(?:\.[A-Za-z0-9-]+)+$/)).max(32)
}).strict();

const appCatalogReleaseSchema = z.object({
  version: semverSchema,
  hostApiVersion: hostApiVersionSchema,
  minWorkbenchVersion: semverSchema,
  url: z.string().url().refine((value) => {
    const url = new URL(value);
    return url.protocol === 'https:' && !url.username && !url.password && !url.hash && (!url.port || url.port === '443');
  }, '应用包地址必须是安全的 HTTPS 地址'),
  size: z.number().int().positive().max(200 * 1024 * 1024),
  sha256: z.string().regex(/^[0-9a-f]{64}$/i, 'SHA-256 格式无效'),
  signature: z.object({
    keyId: z.string().min(1).max(64).regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/),
    signature: z.string().min(1)
  }).strict()
}).strict();

const appCatalogItemSchema = z.object({
  id: identifierSchema,
  name: z.string().trim().min(1),
  description: z.string().trim().min(1),
  publisherId: identifierSchema,
  releases: z.array(appCatalogReleaseSchema).min(1)
}).strict();

const appCatalogSchema = z.object({
  schemaVersion: z.literal(1),
  apps: z.array(appCatalogItemSchema)
}).strict();

export function parseAppManifest(value: unknown): AppManifestV1 {
  const parsed = appManifestSchema.safeParse(value);
  if (!parsed.success) throw new Error(formatValidationError('应用 manifest', parsed.error));
  assertSafeAppArchiveEntry(parsed.data.runtime.rendererEntry, '应用 renderer 入口路径');
  if ('backendEntry' in parsed.data.runtime) assertSafeAppArchiveEntry(parsed.data.runtime.backendEntry, '应用 backend 入口路径');
  assertSafeAppArchiveEntry(parsed.data.runtime.icon, '应用图标入口路径');
  return parsed.data;
}

export function parseAppCatalog(value: unknown): AppCatalogDocumentV1 {
  const parsed = appCatalogSchema.safeParse(value);
  if (!parsed.success) throw new Error(formatValidationError('应用目录', parsed.error));
  const appIds = new Set<string>();
  for (const app of parsed.data.apps) {
    if (appIds.has(app.id)) throw new Error(`应用目录包含重复应用：${app.id}`);
    appIds.add(app.id);
    const versions = new Set<string>();
    for (const item of app.releases) {
      if (versions.has(item.version)) throw new Error(`应用目录包含重复版本：${app.id}@${item.version}`);
      versions.add(item.version);
    }
  }
  return parsed.data;
}

export function isCompatibleAppRelease(release: AppCatalogRelease, workbenchVersion: string, hostApiVersion: string): boolean {
  if (release.version.includes('-')) return false;
  const minimum = parseSemver(release.minWorkbenchVersion);
  const current = parseSemver(workbenchVersion);
  if (!minimum || !current || compareSemver(current, minimum) < 0) return false;
  const requiredApi = parseApiVersion(release.hostApiVersion);
  const currentApi = parseApiVersion(hostApiVersion);
  return Boolean(requiredApi && currentApi && requiredApi.major === currentApi.major && currentApi.minor >= requiredApi.minor);
}

export function compareAppVersions(left: string, right: string): number {
  const leftVersion = parseSemver(left);
  const rightVersion = parseSemver(right);
  if (!leftVersion || !rightVersion) throw new Error('应用版本号无效');
  return compareSemver(leftVersion, rightVersion);
}

export function verifyAppReleasePayload(payload: Uint8Array, release: AppCatalogRelease, trustedKeys: Record<string, KeyObject | string>): void {
  if (payload.byteLength !== release.size) throw new Error(`应用包大小校验失败：目录声明 ${release.size} 字节，实际 ${payload.byteLength} 字节`);
  const digest = createHash('sha256').update(payload).digest('hex');
  if (digest.toLowerCase() !== release.sha256.toLowerCase()) throw new Error('应用包 SHA-256 校验失败');
  const publicKey = trustedKeys[release.signature.keyId];
  if (!publicKey) throw new Error(`应用包签名密钥不受信任：${release.signature.keyId}`);
  const encodedSignature = release.signature.signature;
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(encodedSignature)) throw new Error('应用包签名格式无效');
  const signature = Buffer.from(encodedSignature, 'base64');
  if (signature.length !== 64 || !verify(null, payload, publicKey, signature)) throw new Error('应用包签名校验失败');
}

export function assertSafeAppArchiveEntry(entry: string, label = '应用包路径'): void {
  const normalized = entry.replaceAll('\\', '/');
  const segments = normalized.split('/');
  if (!entry || entry.includes('\0') || isAbsolute(entry) || normalized.startsWith('/') || segments.includes('..') || segments.includes('')) {
    throw new Error(`${label}包含不安全路径：${entry}`);
  }
}

function formatValidationError(label: string, error: z.ZodError): string {
  const unknown = error.issues.find((issue) => issue.code === 'unrecognized_keys');
  if (unknown && unknown.code === 'unrecognized_keys') return `${label}包含未知字段：${unknown.keys.join('、')}`;
  const first = error.issues[0];
  return `${label}格式无效${first?.message ? `：${first.message}` : ''}`;
}

interface SemverParts { major: number; minor: number; patch: number; prerelease: string[] }

function parseSemver(value: string): SemverParts | undefined {
  const match = value.match(/^(\d+)\.(\d+)\.(\d+)(?:-([^+]+))?/);
  if (!match) return undefined;
  return { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]), prerelease: match[4]?.split('.') ?? [] };
}

function compareSemver(left: SemverParts, right: SemverParts): number {
  for (const key of ['major', 'minor', 'patch'] as const) {
    if (left[key] !== right[key]) return left[key] > right[key] ? 1 : -1;
  }
  if (!left.prerelease.length && !right.prerelease.length) return 0;
  if (!left.prerelease.length) return 1;
  if (!right.prerelease.length) return -1;
  return left.prerelease.join('.').localeCompare(right.prerelease.join('.'));
}

function parseApiVersion(value: string): { major: number; minor: number } | undefined {
  const match = value.match(/^(\d+)\.(\d+)$/);
  return match ? { major: Number(match[1]), minor: Number(match[2]) } : undefined;
}
