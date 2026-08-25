import { mkdir, rm, writeFile } from 'node:fs/promises';
import { basename, join } from 'node:path';
import * as tar from 'tar';
import { isDiagnosticPackagePath } from '../domain/diagnostic-package';
import {
  analyzeExtractedDirectory,
  type AnalysisResult,
  type AnalyzerRuleConfig
} from './log-analyzer';
import { analyzeStructuredExtract, type StructuredAnalysis } from './structured-analysis';

export type AnalysisScope = 'comprehensive' | 'storage';

export interface ArchiveAnalysisRequest {
  sourcePath: string;
  extractDirectory: string;
  rules: AnalyzerRuleConfig;
  scope?: AnalysisScope;
  onProgress?: (progress: { progress: number; message: string }) => void;
}

export interface ArchiveAnalysisResult {
  analysis: AnalysisResult;
  structured: StructuredAnalysis;
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

  request.onProgress?.({ progress: 5, message: '正在准备诊断包' });

  await rm(request.extractDirectory, { recursive: true, force: true });
  await mkdir(request.extractDirectory, { recursive: true });

  try {
    request.onProgress?.({ progress: 12, message: '正在解压诊断包' });
    await tar.x({ file: request.sourcePath, cwd: request.extractDirectory, gzip: true, strict: true });
  } catch (error) {
    throw new Error(`无法解压诊断包：${error instanceof Error ? error.message : String(error)}`);
  }

  request.onProgress?.({ progress: 30, message: '正在扫描日志文件' });
  const analysis = await analyzeExtractedDirectory(request.extractDirectory, request.rules, ({ processedFiles, totalFiles }) => {
    request.onProgress?.({ progress: 30 + Math.round((processedFiles / Math.max(totalFiles, 1)) * 40), message: `正在扫描日志文件（${processedFiles}/${totalFiles}）` });
  });
  request.onProgress?.({ progress: 70, message: '正在分析系统与存储信息' });
  const structured = await analyzeStructuredExtract(request.extractDirectory, analysis);
  request.onProgress?.({ progress: 88, message: '正在生成分析报告' });
  const reportDirectory = join(request.extractDirectory, 'Report');
  const reportPath = join(reportDirectory, 'index.html');
  await writeReportArtifacts(reportDirectory, basename(request.sourcePath), analysis, structured, request.scope ?? 'comprehensive');
  request.onProgress?.({ progress: 98, message: '正在完成报告索引' });

  return { analysis, structured, reportPath };
}

/** 保留插件的 Report/static 与 Report/structured 目录约定，所有报告产物均可独立打开。 */
async function writeReportArtifacts(directory: string, sourceName: string, analysis: AnalysisResult, structured: StructuredAnalysis, scope: AnalysisScope): Promise<void> {
  const staticDirectory = join(directory, 'static');
  const structuredDirectory = join(directory, 'structured');
  await mkdir(staticDirectory, { recursive: true });
  await mkdir(structuredDirectory, { recursive: true });
  await Promise.all([
    writeFile(join(staticDirectory, 'workbench-report.css'), reportCss, 'utf8'),
    writeFile(join(structuredDirectory, 'analysis.json'), JSON.stringify(analysis, null, 2), 'utf8'),
    writeFile(join(structuredDirectory, 'storage-health.json'), JSON.stringify(structured, null, 2), 'utf8'),
    writeFile(join(structuredDirectory, 'sysinfo.json'), JSON.stringify(structured.sysInfo, null, 2), 'utf8'),
    writeFile(join(structuredDirectory, 'network.json'), JSON.stringify(structured.networks, null, 2), 'utf8'),
    writeFile(join(structuredDirectory, 'lsblk.txt'), structured.blockDevicesRaw, 'utf8'),
    writeFile(join(directory, 'lsblk.html'), renderListPage('块设备信息', structured.blockDevices), 'utf8'),
    writeFile(join(directory, 'index.html'), renderFullReport(sourceName, analysis, structured, scope), 'utf8')
  ]);
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

function renderListPage(title: string, rows: string[]): string { return `<!doctype html><meta charset="utf-8"><link rel="stylesheet" href="static/workbench-report.css"><main><h1>${escapeHtml(title)}</h1><pre>${escapeHtml(rows.join('\n') || '未提供数据')}</pre></main>`; }
function renderFullReport(sourceName: string, analysis: AnalysisResult, data: StructuredAnalysis, scope: AnalysisScope): string {
  const issues = analysis.files.flatMap((file) => file.issues.map((issue) => ({ ...issue, file: file.file, category: file.category })));
  const issueHtml = scope === 'storage' ? '' : issues.map((issue) => `<article class="issue ${escapeHtml(issue.severity)}"><b>${escapeHtml(issue.message)}</b><small>${escapeHtml(issue.category)} · ${escapeHtml(issue.file)} · 第 ${issue.line} 行</small><pre>${escapeHtml(issue.contextLines.map((line) => `${line.number}: ${line.text}`).join('\n'))}</pre></article>`).join('') || '<p>未发现规则命中。</p>';
  const disks = data.disks.map((disk) => `<article class="disk ${disk.health}"><h3>${escapeHtml(disk.device)}</h3><p>状态：${escapeHtml(disk.health)} · 证据 ${disk.evidence.length} 条</p><details><summary>SMART 与证据</summary><pre>${escapeHtml([...disk.evidence, ...disk.smart.map((item) => `${item.id} ${item.name} ${item.status}`)].join('\n') || '未提供')}</pre></details></article>`).join('') || '<p>未识别到物理硬盘快照。</p>';
  return `<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="static/workbench-report.css"><title>系统诊断报告</title><main><header><h1>系统诊断报告</h1><p>${escapeHtml(sourceName)} · ${scope === 'storage' ? '存储健康分析' : '综合分析'}</p><strong class="health ${data.overallHealth}">系统状态：${data.overallHealth}</strong></header><section class="metrics"><div>规则问题<strong>${issues.length}</strong></div><div>存储证据<strong>${data.evidence.length}</strong></div><div>物理硬盘<strong>${data.disks.length}</strong></div><div>网络接口<strong>${data.networks.length}</strong></div></section><section><h2>故障摘要与处理建议</h2><p>${escapeHtml(data.customerReply)}</p><ol>${data.recommendations.map((value) => `<li>${escapeHtml(value)}</li>`).join('')}</ol></section><section><h2>存储拓扑与异常链路</h2>${disks}<p><a href="lsblk.html">查看块设备信息</a></p></section><section><h2>系统信息</h2><pre>${escapeHtml(JSON.stringify(data.sysInfo, null, 2).slice(0, 20000) || '未提供')}</pre></section><section><h2>网络与 RAID / 卷</h2><pre>${escapeHtml([...data.networks, ...data.raids, ...data.volumes].join('\n') || '未提供')}</pre></section><section><h2>规则分析结果</h2>${issueHtml}</section></main></html>`;
}
const reportCss = `:root{font-family:"Segoe UI","Microsoft YaHei",sans-serif;color:#182235;background:#f4f7fb}body{margin:0}main{max-width:1240px;margin:auto;padding:28px}header{padding:24px;border-radius:14px;background:#102a56;color:#fff}h1,h2,h3,p{margin-top:0}.health{display:inline-block;padding:6px 10px;border-radius:99px}.health.critical{background:#9e2530}.health.attention{background:#9a6510}.health.normal{background:#23734e}.metrics{display:grid;grid-template-columns:repeat(4,1fr);gap:10px;margin:16px 0}.metrics div,section,.disk{padding:16px;margin:14px 0;border:1px solid #dce5f1;border-radius:10px;background:#fff}.metrics strong{display:block;font-size:26px}pre{padding:12px;overflow:auto;background:#0c1729;color:#e8f0fc;border-radius:7px;white-space:pre-wrap}.issue{border-left:4px solid #c08a00}.issue.critical{border-color:#bd2735}.issue.info{border-color:#2977c8}.issue small{display:block;margin-top:6px;color:#63728c}.disk.critical{border-color:#efb1b8}.disk.attention{border-color:#f5d38d}@media(max-width:720px){.metrics{grid-template-columns:repeat(2,1fr)}main{padding:12px}}`;
