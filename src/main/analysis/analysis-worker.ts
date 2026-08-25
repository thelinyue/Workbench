import { parentPort, workerData } from 'node:worker_threads';
import { runArchiveAnalysis } from './archive-analysis';
import type { AnalyzerRuleConfig } from './log-analyzer';

interface AnalysisWorkerInput {
  sourcePath: string;
  extractDirectory: string;
  rules: AnalyzerRuleConfig;
}

/**
 * 分析中心私有 Worker。
 *
 * 归档解压和日志扫描可能消耗大量 CPU 与磁盘 I/O，必须从 Electron 主线程移出；Worker 只接收
 * 经主进程构造的路径和内置规则，不向渲染层暴露任何可执行能力。
 */
void (async () => {
  try {
    const result = await runArchiveAnalysis(workerData as AnalysisWorkerInput);
    parentPort?.postMessage({ succeeded: true, reportPath: result.reportPath });
  } catch (error) {
    parentPort?.postMessage({ succeeded: false, errorMessage: error instanceof Error ? error.message : String(error) });
  }
})();
