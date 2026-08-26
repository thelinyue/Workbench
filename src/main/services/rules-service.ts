import { createHash, verify, type KeyObject } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { z } from 'zod';

export interface AnalyzerRuleKeyword {
  term: string;
  result: string;
  regex?: boolean;
  severity?: 'critical' | 'warning' | 'info';
  context_lines?: number;
  context_direction?: 'up' | 'down';
  search_direction?: 'up' | 'down';
}

export interface AnalyzerRuleCatalog {
  tgz: { version?: string; files: Array<{ name: string; category: string; file_patterns?: string[]; keywords: AnalyzerRuleKeyword[] }> };
  zip: { version?: string; files: Array<{ name: string; category: string; file_patterns?: string[]; keywords: AnalyzerRuleKeyword[] }> };
}

interface UserRule {
  localId?: string;
  file?: string;
  category?: string;
  status?: string;
  rule?: AnalyzerRuleKeyword;
}

interface UserState {
  schemaVersion: number;
  baseVersion: string | null;
  categories: string[];
  rules: UserRule[];
}

interface OfficialState {
  rules: AnalyzerRuleCatalog;
  source: 'bundled' | 'downloaded';
  version: string;
}

export interface RulesServiceOptions {
  rootDirectory: string;
  officialRules: AnalyzerRuleCatalog;
  catalogUrl?: string;
  trustedKeys?: Record<string, KeyObject | string>;
  fetchImpl?: typeof fetch;
}

const RULE_SET_ID = 'analysis-center-rules';
const MAX_CATALOG_BYTES = 128 * 1024;
const MAX_PACKAGE_BYTES = 2 * 1024 * 1024;
const VERSION_PATTERN = /^\d{4}\.\d{2}\.\d{2}$/;

const keywordSchema = z.object({
  term: z.string().min(1).max(4096),
  result: z.string().min(1).max(4096),
  regex: z.boolean().optional(),
  severity: z.enum(['critical', 'warning', 'info']).optional(),
  context_lines: z.number().int().min(0).max(1000).optional(),
  context_direction: z.enum(['up', 'down']).optional(),
  search_direction: z.enum(['up', 'down']).optional()
}).strict();
const fileSchema = z.object({
  name: z.string().min(1).max(255),
  category: z.string().min(1).max(128),
  file_patterns: z.array(z.string().min(1).max(1024)).max(32).optional(),
  keywords: z.array(keywordSchema).max(4096)
}).strict();
const formatSchema = z.object({ files: z.array(fileSchema).max(512) }).strict();
const packageSchema = z.object({
  schemaVersion: z.literal(1),
  ruleSetId: z.literal(RULE_SET_ID),
  version: z.string().regex(VERSION_PATTERN),
  tgz: formatSchema,
  zip: formatSchema
}).strict();
const catalogSchema = z.object({
  schemaVersion: z.literal(1),
  ruleSetId: z.literal(RULE_SET_ID),
  version: z.string().regex(VERSION_PATTERN),
  packageUrl: z.string().url().refine((value) => new URL(value).protocol === 'https:', 'packageUrl 必须使用 HTTPS'),
  packageSize: z.number().int().positive().max(MAX_PACKAGE_BYTES),
  sha256: z.string().regex(/^[0-9a-f]{64}$/),
  signatureAlgorithm: z.literal('Ed25519'),
  keyId: z.string().min(1).max(64).regex(/^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/),
  signature: z.string().min(1).max(256),
  releaseNotesUrl: z.string().url().refine((value) => new URL(value).protocol === 'https:', 'releaseNotesUrl 必须使用 HTTPS').optional()
}).strict();

type RulePackage = z.infer<typeof packageSchema>;
type RuleCatalog = z.infer<typeof catalogSchema>;

/**
 * 宿主统一管理官方规则和用户增量规则。
 *
 * 在线包只有在目录、哈希、签名和规则内容全部通过校验后才会进入版本目录并切换当前版本；
 * 任何失败都会继续使用内存中的最后可用规则，分析任务拿到的始终是完整快照。
 */
export class RulesService {
  private readonly userPath: string;
  private readonly activePath: string;
  private readonly officialDirectory: string;
  private readonly currentOfficialPath: string;
  private officialStatePromise?: Promise<OfficialState>;
  private lock: Promise<void> = Promise.resolve();

  public constructor(private readonly options: RulesServiceOptions) {
    this.userPath = join(options.rootDirectory, 'Local', 'additions.json');
    this.activePath = join(options.rootDirectory, 'Active', 'active.json');
    this.officialDirectory = join(options.rootDirectory, 'Official');
    this.currentOfficialPath = join(this.officialDirectory, 'current.json');
  }

  public async invoke(method: string, payload: unknown): Promise<{ type: string; data?: unknown; message?: string }> {
    if (method === 'rules.getRuleState') return this.withLock(async () => ({ type: 'ruleState', data: await this.getState() }));
    if (method === 'rules.getActive') return this.withLock(async () => ({ type: 'activeRules', data: await this.getActive() }));
    if (method === 'rules.getUpdateState') return this.withLock(async () => ({ type: 'updateState', data: await this.getUpdateState() }));
    if (method === 'rules.updateOfficial') return { type: 'officialUpdate', data: await this.updateOfficial() };
    if (method === 'rules.validateUserRules') return { type: 'validationResult', data: { issues: this.validate(readUser(payload, 'user')) } };
    if (method === 'rules.saveUserRules') return this.withLock(async () => ({ type: 'saveSucceeded', data: await this.save(readUser(payload, 'user')) }));
    if (method === 'rules.exportRules') return this.withLock(async () => ({ type: 'exportData', data: (await this.getState()).user }));
    if (method === 'rules.submitSelectedRules') return this.withLock(async () => ({ type: 'submissionSucceeded', data: { state: await this.save(readUser(payload, 'user')) } }));
    throw new Error(`规则服务不支持该请求：${method}`);
  }

  private async getState(): Promise<{ official: { version: string; files: unknown[] }; user: UserState; state: { localRuleCount: number; pendingRuleCount: number } }> {
    const official = await this.getOfficialState();
    const user = await this.loadUser(official.version);
    return { official: { version: official.version, files: official.rules.tgz.files }, user, state: { localRuleCount: user.rules.length, pendingRuleCount: user.rules.filter((item) => item.status === 'pending').length } };
  }

  private async getUpdateState(): Promise<{ ruleSetId: string; currentVersion: string; source: 'bundled' | 'downloaded' }> {
    const official = await this.getOfficialState();
    return { ruleSetId: RULE_SET_ID, currentVersion: official.version, source: official.source };
  }

  private async getActive(): Promise<AnalyzerRuleCatalog> {
    const official = await this.getOfficialState();
    const user = await this.loadUser(official.version);
    const active = mergeRules(official.rules, user);
    await atomicWriteFile(this.activePath, Buffer.from(`${JSON.stringify(active, null, 2)}\n`));
    return active;
  }

  /** 下载和验签不占用激活锁，只有最终落盘与内存切换是串行的。 */
  private async updateOfficial(): Promise<{ status: 'updated' | 'up-to-date'; previousVersion: string; currentVersion: string }> {
    const previous = await this.withLock(async () => (await this.getOfficialState()).version);
    const catalogUrl = this.options.catalogUrl;
    if (!catalogUrl || new URL(catalogUrl).protocol !== 'https:') throw new Error('官方规则目录地址必须使用 HTTPS。');
    const catalog = parseCatalog(await this.download(catalogUrl, MAX_CATALOG_BYTES, '规则目录'));
    if (compareRuleVersions(catalog.version, previous) <= 0) return { status: 'up-to-date', previousVersion: previous, currentVersion: previous };
    const packageBytes = await this.download(catalog.packageUrl, MAX_PACKAGE_BYTES, '规则包');
    const rulePackage = verifyRulePackage(packageBytes, catalog, this.options.trustedKeys ?? {});
    return this.withLock(async () => {
      const current = await this.getOfficialState();
      if (compareRuleVersions(rulePackage.version, current.version) <= 0) return { status: 'up-to-date', previousVersion: current.version, currentVersion: current.version };
      await atomicWriteFile(join(this.officialDirectory, 'versions', `${rulePackage.version}.json`), packageBytes);
      await atomicWriteFile(this.currentOfficialPath, Buffer.from(`${JSON.stringify(catalog, null, 2)}\n`));
      this.officialStatePromise = Promise.resolve({ rules: toAnalyzerCatalog(rulePackage), source: 'downloaded', version: rulePackage.version });
      console.info(`官方分析规则已更新：${current.version} -> ${rulePackage.version}。`);
      return { status: 'updated', previousVersion: current.version, currentVersion: rulePackage.version };
    });
  }

  private async download(url: string, maximumBytes: number, label: string): Promise<Buffer> {
    const fetchImpl = this.options.fetchImpl ?? fetch;
    let response: Response;
    try { response = await fetchImpl(url, { signal: AbortSignal.timeout(15_000) }); }
    catch (error) { throw new Error(`${label}下载失败：${error instanceof Error ? error.message : String(error)}`); }
    if (!response.ok) throw new Error(`${label}下载失败：HTTP ${response.status}。`);
    const declaredLength = Number(response.headers.get('content-length') ?? 0);
    if (declaredLength > maximumBytes) throw new Error(`${label}超过允许的大小上限。`);
    const bytes = Buffer.from(await response.arrayBuffer());
    if (bytes.byteLength > maximumBytes) throw new Error(`${label}超过允许的大小上限。`);
    return bytes;
  }

  private async getOfficialState(): Promise<OfficialState> {
    this.officialStatePromise ??= this.loadOfficialState();
    return this.officialStatePromise;
  }

  private async loadOfficialState(): Promise<OfficialState> {
    const bundledVersion = this.options.officialRules.tgz.version ?? this.options.officialRules.zip.version ?? '内置规则';
    try {
      const catalog = parseCatalog(await readFile(this.currentOfficialPath));
      const bytes = await readFile(join(this.officialDirectory, 'versions', `${catalog.version}.json`));
      const rulePackage = verifyRulePackage(bytes, catalog, this.options.trustedKeys ?? {});
      return { rules: toAnalyzerCatalog(rulePackage), source: 'downloaded', version: rulePackage.version };
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') console.error(`已下载官方规则无法使用，回退到内置规则：${error instanceof Error ? error.message : String(error)}`);
      return { rules: structuredClone(this.options.officialRules), source: 'bundled', version: bundledVersion };
    }
  }

  private async save(user: UserState): Promise<{ official: { version: string; files: unknown[] }; user: UserState; state: { localRuleCount: number; pendingRuleCount: number } }> {
    const issues = this.validate(user);
    if (issues.some((item) => item.severity === 'error')) throw new Error('规则校验未通过，请修正错误后再保存。');
    await atomicWriteFile(this.userPath, Buffer.from(`${JSON.stringify(user, null, 2)}\n`));
    await this.getActive();
    return this.getState();
  }

  private async loadUser(baseVersion: string): Promise<UserState> {
    try {
      const value = JSON.parse(await readFile(this.userPath, 'utf8')) as Partial<UserState>;
      return { schemaVersion: 1, baseVersion: value.baseVersion ?? null, categories: Array.isArray(value.categories) ? value.categories : [], rules: Array.isArray(value.rules) ? value.rules : [] };
    } catch {
      return { schemaVersion: 1, baseVersion, categories: [], rules: [] };
    }
  }

  private validate(user: UserState): Array<{ localIds: string[]; severity: 'error' | 'warning'; field: string; message: string }> {
    const issues: Array<{ localIds: string[]; severity: 'error' | 'warning'; field: string; message: string }> = [];
    const seen = new Set<string>();
    for (const item of user.rules) {
      const id = item.localId ?? '';
      const rule = item.rule;
      if (!item.file?.trim() || !item.category?.trim() || !rule?.term?.trim() || !rule.result?.trim()) issues.push({ localIds: [id], severity: 'error', field: 'rule', message: '规则文件、分类、关键词和问题描述不能为空。' });
      const key = `${item.file ?? ''}\u0000${rule?.term ?? ''}\u0000${Boolean(rule?.regex)}`;
      if (seen.has(key)) issues.push({ localIds: [id], severity: 'error', field: 'term', message: '存在重复规则。' });
      seen.add(key);
      if (rule?.regex) {
        try { new RegExp(rule.term); } catch { issues.push({ localIds: [id], severity: 'error', field: 'term', message: '正则表达式无效。' }); }
      }
    }
    return issues;
  }

  private async withLock<T>(action: () => Promise<T>): Promise<T> {
    const previous = this.lock;
    let release!: () => void;
    this.lock = new Promise<void>((resolve) => { release = resolve; });
    await previous;
    try { return await action(); } finally { release(); }
  }
}

function parseCatalog(bytes: Uint8Array): RuleCatalog {
  if (bytes.byteLength > MAX_CATALOG_BYTES) throw new Error('规则目录超过允许的大小上限。');
  try { return catalogSchema.parse(JSON.parse(Buffer.from(bytes).toString('utf8'))); }
  catch (error) { throw new Error(`规则目录格式无效：${formatValidationError(error)}`); }
}

function verifyRulePackage(bytes: Uint8Array, catalog: RuleCatalog, trustedKeys: Record<string, KeyObject | string>): RulePackage {
  if (bytes.byteLength !== catalog.packageSize) throw new Error('规则包大小与目录不一致。');
  if (createHash('sha256').update(bytes).digest('hex') !== catalog.sha256) throw new Error('规则包 SHA-256 与目录不一致。');
  const publicKey = trustedKeys[catalog.keyId];
  if (!publicKey) throw new Error(`规则包签名密钥不受信任：${catalog.keyId}。`);
  if (!verify(null, bytes, publicKey, decodeSignature(catalog.signature))) throw new Error('规则包 Ed25519 签名校验失败。');
  let rulePackage: RulePackage;
  try { rulePackage = packageSchema.parse(JSON.parse(Buffer.from(bytes).toString('utf8'))); }
  catch (error) { throw new Error(`规则包格式无效：${formatValidationError(error)}`); }
  if (rulePackage.version !== catalog.version) throw new Error('规则包版本与目录不一致。');
  validateRuleContents(rulePackage);
  return rulePackage;
}

function validateRuleContents(rulePackage: RulePackage): void {
  for (const format of ['tgz', 'zip'] as const) {
    const seenFiles = new Set<string>();
    for (const file of rulePackage[format].files) {
      const normalizedName = file.name.toLowerCase();
      if (seenFiles.has(normalizedName)) throw new Error(`规则包存在重复文件定义：${format}/${file.name}。`);
      seenFiles.add(normalizedName);
      for (const pattern of file.file_patterns ?? []) {
        try { new RegExp(pattern, 'i'); } catch { throw new Error(`规则包文件匹配正则无效：${format}/${file.name}。`); }
      }
      const seenRules = new Set<string>();
      for (const keyword of file.keywords) {
        const key = `${keyword.term}\u0000${Boolean(keyword.regex)}`;
        if (seenRules.has(key)) throw new Error(`规则包存在重复规则：${format}/${file.name}/${keyword.term}。`);
        seenRules.add(key);
        if (keyword.regex) {
          try { new RegExp(keyword.term, 'i'); } catch { throw new Error(`规则包关键词正则无效：${format}/${file.name}/${keyword.term}。`); }
        }
      }
    }
  }
}

function decodeSignature(value: string): Buffer {
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(value) || value.length % 4 !== 0) throw new Error('规则包签名格式无效。');
  const signature = Buffer.from(value, 'base64');
  if (signature.byteLength !== 64 || signature.toString('base64') !== value) throw new Error('规则包签名格式无效。');
  return signature;
}

function toAnalyzerCatalog(rulePackage: RulePackage): AnalyzerRuleCatalog {
  return {
    tgz: { version: rulePackage.version, files: structuredClone(rulePackage.tgz.files) },
    zip: { version: rulePackage.version, files: structuredClone(rulePackage.zip.files) }
  };
}

function formatValidationError(error: unknown): string {
  if (error instanceof z.ZodError) return error.issues.map((issue) => {
    const location = issue.path.join('.') || 'root';
    if (issue.code === 'unrecognized_keys') return `${location} 包含未知字段：${issue.keys.join(', ')}`;
    return `${location} ${issue.message}`;
  }).join('；');
  return error instanceof Error ? error.message : String(error);
}

function compareRuleVersions(left: string, right: string): number {
  if (!VERSION_PATTERN.test(left) || !VERSION_PATTERN.test(right)) return left === right ? 0 : VERSION_PATTERN.test(left) ? 1 : -1;
  const leftParts = left.split('.').map(Number);
  const rightParts = right.split('.').map(Number);
  for (let index = 0; index < leftParts.length; index += 1) {
    if (leftParts[index] !== rightParts[index]) return leftParts[index]! - rightParts[index]!;
  }
  return 0;
}

async function atomicWriteFile(path: string, bytes: Uint8Array): Promise<void> {
  await mkdir(dirname(path), { recursive: true });
  const temporaryPath = `${path}.${process.pid}.${Date.now()}.tmp`;
  try {
    await writeFile(temporaryPath, bytes);
    await rename(temporaryPath, path);
  } finally {
    await rm(temporaryPath, { force: true });
  }
}

function readUser(payload: unknown, key: string): UserState {
  if (!payload || typeof payload !== 'object' || Array.isArray(payload)) throw new Error('规则请求参数必须是对象。');
  const value = (payload as Record<string, unknown>)[key];
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('规则请求缺少用户规则。');
  return value as UserState;
}

function mergeRules(official: AnalyzerRuleCatalog, user: UserState): AnalyzerRuleCatalog {
  const result = structuredClone(official);
  for (const item of user.rules) {
    if (item.status === 'rejected' || item.status === 'conflict' || !item.file || !item.category || !item.rule) continue;
    for (const format of ['tgz', 'zip'] as const) {
      let target = result[format].files.find((file) => file.name === item.file);
      if (!target) { target = { name: item.file, category: item.category, keywords: [] }; result[format].files.push(target); }
      if (!target.keywords.some((rule) => rule.term === item.rule!.term && Boolean(rule.regex) === Boolean(item.rule!.regex))) target.keywords.push({ ...item.rule, result: item.rule.result });
    }
  }
  return result;
}
