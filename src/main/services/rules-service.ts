import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

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

export interface RulesServiceOptions {
  rootDirectory: string;
  officialRules: AnalyzerRuleCatalog;
}

/**
 * 宿主提供版本化规则 API，应用只提交用户增量，不直接访问文件系统。
 * Active 快照由宿主一次性生成，分析中心把它复制到任务 Worker，保证规则变更不会影响运行中的任务。
 */
export class RulesService {
  private readonly userPath: string;
  private readonly activePath: string;

  public constructor(private readonly options: RulesServiceOptions) {
    this.userPath = join(options.rootDirectory, 'Local', 'additions.json');
    this.activePath = join(options.rootDirectory, 'Active', 'active.json');
  }

  public async invoke(method: string, payload: unknown): Promise<{ type: string; data?: unknown; message?: string }> {
    if (method === 'rules.getRuleState') return { type: 'ruleState', data: await this.getState() };
    if (method === 'rules.getActive') return { type: 'activeRules', data: await this.getActive() };
    if (method === 'rules.validateUserRules') return { type: 'validationResult', data: { issues: this.validate(readUser(payload, 'user')) } };
    if (method === 'rules.saveUserRules') return { type: 'saveSucceeded', data: await this.save(readUser(payload, 'user')) };
    if (method === 'rules.exportRules') return { type: 'exportData', data: (await this.getState()).user };
    if (method === 'rules.submitSelectedRules') return { type: 'submissionSucceeded', data: { state: await this.save(readUser(payload, 'user')) } };
    throw new Error(`规则服务不支持该请求：${method}`);
  }

  private async getState(): Promise<{ official: { version: string; files: unknown[] }; user: UserState; state: { localRuleCount: number; pendingRuleCount: number } }> {
    const user = await this.loadUser();
    return { official: { version: this.options.officialRules.tgz.version ?? '内置规则', files: this.options.officialRules.tgz.files }, user, state: { localRuleCount: user.rules.length, pendingRuleCount: user.rules.filter((item) => item.status === 'pending').length } };
  }

  private async getActive(): Promise<AnalyzerRuleCatalog> {
    const user = await this.loadUser();
    const active = mergeRules(this.options.officialRules, user);
    await mkdir(join(this.options.rootDirectory, 'Active'), { recursive: true });
    await writeFile(this.activePath, `${JSON.stringify(active, null, 2)}\n`, 'utf8');
    return active;
  }

  private async save(user: UserState): Promise<{ official: { version: string; files: unknown[] }; user: UserState; state: { localRuleCount: number; pendingRuleCount: number } }> {
    const issues = this.validate(user);
    if (issues.some((item) => item.severity === 'error')) throw new Error('规则校验未通过，请修正错误后再保存。');
    await mkdir(join(this.options.rootDirectory, 'Local'), { recursive: true });
    await writeFile(this.userPath, `${JSON.stringify(user, null, 2)}\n`, 'utf8');
    await this.getActive();
    return this.getState();
  }

  private async loadUser(): Promise<UserState> {
    try {
      const value = JSON.parse(await readFile(this.userPath, 'utf8')) as Partial<UserState>;
      return { schemaVersion: 1, baseVersion: value.baseVersion ?? null, categories: Array.isArray(value.categories) ? value.categories : [], rules: Array.isArray(value.rules) ? value.rules : [] };
    } catch {
      return { schemaVersion: 1, baseVersion: this.options.officialRules.tgz.version ?? null, categories: [], rules: [] };
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
