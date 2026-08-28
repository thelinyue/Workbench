export interface AnalysisCenterImportBridge {
  getDroppedFilePaths(files: File[]): string[];
  invoke(appId: string, method: string, payload?: unknown): Promise<unknown>;
}

/**
 * 将浏览器 File 对象转换后的受控路径逐个交给分析中心。
 *
 * 桌面拖放和分析中心 iframe 拖放共用这一边界：文件路径只能由预加载桥接取得，单个文件
 * 失败不会终止该批次。是否自动开始分析由入口明确决定，防止桌面拖放意外占用分析队列。
 */
export async function importAnalysisCenterFiles(
  files: File[],
  bridge: AnalysisCenterImportBridge,
  startAnalysis: boolean
): Promise<{ importedCount: number; failures: string[] }> {
  let importedCount = 0;
  const failures: string[] = [];
  for (const sourcePath of bridge.getDroppedFilePaths(files)) {
    try {
      const imported = await bridge.invoke('analysis-center', 'packages.import', { sourcePath }) as { id: string };
      importedCount += 1;
      if (startAnalysis) await bridge.invoke('analysis-center', 'analysis.start', { packageId: imported.id });
    } catch (error) {
      const fileName = sourcePath.replace(/^.*[\\/]/, '');
      failures.push(`${fileName}：${error instanceof Error ? error.message : String(error)}`);
    }
  }
  return { importedCount, failures };
}
