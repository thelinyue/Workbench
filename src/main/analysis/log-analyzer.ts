import { readdir, readFile, stat } from 'node:fs/promises';
import { basename, join, relative } from 'node:path';

export type IssueSeverity = 'critical' | 'warning' | 'info';

/** 由现有系统诊断插件 config.json 演化而来的内置规则格式。 */
export interface AnalyzerRuleConfig {
  version?: string;
  files: Array<{
    name: string;
    category: string;
    keywords: Array<{
      term: string;
      result: string;
      regex?: boolean;
      severity?: IssueSeverity;
      context_lines?: number;
      context_direction?: 'up' | 'down';
      search_direction?: 'up' | 'down';
    }>;
  }>;
}

export interface AnalysisContextLine {
  number: number;
  text: string;
  hit: boolean;
}

export interface AnalysisIssue {
  keyword: string;
  message: string;
  severity: IssueSeverity;
  line: number;
  contextLines: AnalysisContextLine[];
}

export interface AnalysisFileResult {
  file: string;
  category: string;
  issues: AnalysisIssue[];
}

export interface AnalysisResult {
  files: AnalysisFileResult[];
}

/**
 * 分析中心的基础规则扫描器。
 *
 * 它保留系统诊断插件的“文件名 → 分类 → 关键词 → 上下文”模型，方便后续继续迁移
 * 结构化 sysinfo 与存储健康分析；同时保持纯函数边界，供 Worker 和测试共同调用。
 */
export async function analyzeExtractedDirectory(
  extractDirectory: string,
  rules: AnalyzerRuleConfig
): Promise<AnalysisResult> {
  const files = await listFiles(extractDirectory);
  const fileRules = new Map(rules.files.map((item) => [item.name.toLowerCase(), item]));
  const results: AnalysisFileResult[] = [];

  for (const filePath of files) {
    const rule = fileRules.get(basename(filePath).toLowerCase());
    if (!rule) continue;

    const content = await readFile(filePath, 'utf8');
    const issues = matchRules(content, rule.keywords);
    if (issues.length === 0) continue;

    results.push({
      file: relative(extractDirectory, filePath).replaceAll('\\', '/'),
      category: rule.category,
      issues
    });
  }

  return { files: results };
}

async function listFiles(directory: string): Promise<string[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  const result: string[] = [];

  for (const entry of entries) {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      result.push(...await listFiles(path));
      continue;
    }
    if ((await stat(path)).isFile()) result.push(path);
  }

  return result.sort((left, right) => left.localeCompare(right));
}

function matchRules(
  content: string,
  keywords: AnalyzerRuleConfig['files'][number]['keywords']
): AnalysisIssue[] {
  const lines = content.split(/\r?\n/);
  const issues: AnalysisIssue[] = [];

  for (const keyword of keywords) {
    const matcher = createMatcher(keyword);
    const lineIndexes = keyword.search_direction === 'up'
      ? Array.from({ length: lines.length }, (_, index) => lines.length - index - 1)
      : Array.from({ length: lines.length }, (_, index) => index);
    for (const index of lineIndexes) {
      const line = lines[index];
      if (!matcher(line)) continue;
      issues.push({
        keyword: keyword.term,
        message: keyword.result,
        severity: keyword.severity ?? 'warning',
        line: index + 1,
        contextLines: buildContext(lines, index, keyword.context_lines ?? 0, keyword.context_direction ?? 'down')
      });
    }
  }

  return issues;
}

function createMatcher(keyword: AnalyzerRuleConfig['files'][number]['keywords'][number]): (line: string) => boolean {
  if (keyword.regex) {
    const pattern = new RegExp(keyword.term, 'i');
    return (line) => pattern.test(line);
  }
  const term = keyword.term.toLocaleLowerCase();
  return (line) => line.toLocaleLowerCase().includes(term);
}

function buildContext(
  lines: string[],
  hitIndex: number,
  amount: number,
  direction: 'up' | 'down'
): AnalysisContextLine[] {
  const start = direction === 'up' ? Math.max(0, hitIndex - amount) : hitIndex;
  const end = direction === 'down' ? Math.min(lines.length - 1, hitIndex + amount) : hitIndex;
  const context: AnalysisContextLine[] = [];

  for (let index = start; index <= end; index += 1) {
    context.push({ number: index + 1, text: lines[index], hit: index === hitIndex });
  }
  return context;
}
