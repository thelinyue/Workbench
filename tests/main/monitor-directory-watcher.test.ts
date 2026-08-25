import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AnalysisCenterService } from '../../src/main/services/analysis-center-service';
import { MonitorDirectoryWatcher } from '../../src/main/services/monitor-directory-watcher';

const directories: string[] = [];

afterEach(async () => {
  vi.useRealTimers();
  await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe('监控目录定时扫描', () => {
  it('按分钟间隔扫描监控目录并通知渲染层', async () => {
    vi.useFakeTimers();
    const root = await mkdtemp(join(tmpdir(), 'workbench-monitor-'));
    directories.push(root);
    const scanMonitorDirectories = vi.fn().mockResolvedValue([{}]);
    const onChanged = vi.fn();
    const watcher = new MonitorDirectoryWatcher({ scanMonitorDirectories } as unknown as AnalysisCenterService, onChanged);

    watcher.watch([root], 1);
    await vi.advanceTimersByTimeAsync(60_000);

    expect(scanMonitorDirectories).toHaveBeenCalledTimes(1);
    expect(onChanged).toHaveBeenCalledTimes(1);
    await watcher.close();
  });
});
