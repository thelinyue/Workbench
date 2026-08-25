import chokidar, { type FSWatcher } from 'chokidar';
import type { AnalysisCenterService } from './analysis-center-service';

/**
 * 监控目录仅负责发现新增诊断包，不承担读取或分析逻辑。
 * 监听失败会作为中文日志/状态错误交由调用方展示，不会影响手动扫描入口。
 */
export class MonitorDirectoryWatcher {
  private watcher: FSWatcher | undefined;
  private scanTimer: NodeJS.Timeout | undefined;
  private scanning = false;

  public constructor(private readonly analysisCenter: AnalysisCenterService, private readonly onChanged: () => void) {}

  public watch(directories: string[], intervalMinutes: number): void {
    void this.close();
    if (directories.length === 0) return;
    this.watcher = chokidar.watch(directories, { ignoreInitial: true, depth: 0, awaitWriteFinish: { stabilityThreshold: 800, pollInterval: 100 } });
    this.watcher.on('add', (path) => {
      void this.analysisCenter.importPackage(path).then(() => this.onChanged()).catch((error: unknown) => {
        console.error(`发现诊断包失败：${messageOf(error)}`);
      });
    });
    this.watcher.on('error', (error) => console.error(`监控目录发生错误：${messageOf(error)}`));
    this.scanTimer = setInterval(() => { void this.scan(); }, intervalMinutes * 60_000);
  }

  public async close(): Promise<void> {
    if (this.scanTimer) clearInterval(this.scanTimer);
    this.scanTimer = undefined;
    const watcher = this.watcher;
    this.watcher = undefined;
    await watcher?.close();
  }

  private async scan(): Promise<void> {
    if (this.scanning) return;
    this.scanning = true;
    try {
      const discovered = await this.analysisCenter.scanMonitorDirectories();
      if (discovered.length > 0) this.onChanged();
    } catch (error) {
      console.error(`自动扫描监控目录失败：${messageOf(error)}`);
    } finally {
      this.scanning = false;
    }
  }
}

function messageOf(error: unknown): string { return error instanceof Error ? error.message : String(error); }
