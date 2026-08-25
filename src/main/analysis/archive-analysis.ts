import { mkdir, rm, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import * as tar from 'tar';
import { isDiagnosticPackagePath } from '../domain/diagnostic-package';
import {
  analyzeExtractedDirectory,
  type AnalysisResult,
  type AnalyzerRuleConfig
} from './log-analyzer';

export interface ArchiveAnalysisRequest {
  sourcePath: string;
  extractDirectory: string;
  rules: AnalyzerRuleConfig;
}

export interface ArchiveAnalysisResult {
  analysis: AnalysisResult;
  reportPath: string;
}

/**
 * 分析中心的归档执行入口。
 *
 * 该函数对应旧日志分析插件的“解压 → 规则扫描 → 固定 Report/index.html”协议，
 * 但不再启动独立插件进程：规则与报告生成均在工作台内部完成。
 */
export async function runArchiveAnalysis(request: ArchiveAnalysisRequest): Promise<ArchiveAnalysisResult> {
  if (!isDiagnosticPackagePath(request.sourcePath)) {
    throw new Error('仅支持 .tgz 或 .tgz.temp 格式的诊断包');
  }

  await rm(request.extractDirectory, { recursive: true, force: true });
  await mkdir(request.extractDirectory, { recursive: true });

  try {
    await tar.x({ file: request.sourcePath, cwd: request.extractDirectory, gzip: true, strict: true });
  } catch (error) {
    throw new Error(`无法解压诊断包：${error instanceof Error ? error.message : String(error)}`);
  }

  const analysis = await analyzeExtractedDirectory(request.extractDirectory, request.rules);
  const reportDirectory = join(request.extractDirectory, 'Report');
  const reportPath = join(reportDirectory, 'index.html');
  await mkdir(reportDirectory, { recursive: true });
  await writeFile(reportPath, renderReport(basename(request.sourcePath), analysis), 'utf8');

  return { analysis, reportPath };
}

/** 报告为静态 HTML，在系统默认浏览器中打开，因此必须编码所有从日志读取的文本。 */
function renderReport(sourceName: string, analysis: AnalysisResult): string {
  const fileCards = analysis.files.length === 0
    ? '<p class="empty">未发现命中当前内置规则的问题。</p>'
    : analysis.files.map((file) => `
      <section class="file-card">
        <h2>${escapeHtml(file.file)}</h2>
        <p class="category">${escapeHtml(file.category)}</p>
        ${file.issues.map((issue) => `
          <article class="issue ${escapeHtml(issue.severity)}">
            <h3>${escapeHtml(issue.message)}</h3>
            <p>规则：${escapeHtml(issue.keyword)} · 第 ${issue.line} 行</p>
            <pre>${issue.contextLines.map((line) => `${line.number}: ${line.text}`).join('\n')}</pre>
          </article>
        `).join('')}
      </section>
    `).join('');

  return `<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>诊断报告 · ${escapeHtml(sourceName)}</title>
<style>:root{font-family:"Segoe UI","Microsoft YaHei",sans-serif;color:#182235;background:#f4f7fb}body{margin:0}.page{max-width:1120px;margin:auto;padding:32px}header{background:#102a56;color:#fff;border-radius:18px;padding:26px}h1{margin:0;font-size:28px}.meta,.category{color:#6b7a90}.file-card{margin-top:18px;background:#fff;border:1px solid #dce5f1;border-radius:14px;padding:20px}.file-card h2{margin:0}.issue{margin-top:12px;border-left:5px solid #d8a100;background:#fffaf0;padding:13px}.issue.critical{border-color:#c93636;background:#fff4f4}.issue.info{border-color:#2878cc;background:#f1f7ff}.issue h3{margin:0}.issue p{color:#50627e}pre{white-space:pre-wrap;background:#0c1729;color:#e8f0fc;padding:12px;border-radius:8px;overflow:auto}.empty{margin-top:18px;background:#fff;border-radius:12px;padding:20px}</style>
</head><body><main class="page"><header><h1>系统诊断报告</h1><p>${escapeHtml(sourceName)}</p></header>${fileCards}</main></body></html>`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>'"]/g, (character) => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    "'": '&#39;',
    '"': '&quot;'
  })[character] ?? character);
}
