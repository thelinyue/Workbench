import { access, mkdtemp, readFile, rm, writeFile, mkdir } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import * as tar from 'tar';
import yazl from 'yazl';
import { afterEach, describe, expect, it } from 'vitest';
import { runArchiveAnalysis } from '../../src/main/analysis/archive-analysis';
import { builtInAnalyzerRules } from '../../src/main/analysis/built-in-rules';

const directories: string[] = [];

afterEach(async () => {
  await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe('诊断包分析执行', () => {
  it('解压 .tgz、按内置规则分析并生成浏览器报告', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-archive-'));
    directories.push(root);
    const sourceDirectory = join(root, 'source');
    const archivePath = join(root, 'device.tgz');
    const extractDirectory = join(root, 'device');
    await mkdir(sourceDirectory);
    await writeFile(join(sourceDirectory, 'kern'), 'nvme I/O Error: controller failed\n', 'utf8');
    await tar.c({ gzip: true, cwd: sourceDirectory, file: archivePath }, ['kern']);

    const result = await runArchiveAnalysis({
      sourcePath: archivePath,
      extractDirectory,
      rules: builtInAnalyzerRules
    });

    await expect(access(result.reportPath)).resolves.toBeUndefined();
    await expect(access(join(extractDirectory, 'Report', 'static', 'workbench-report.css'))).resolves.toBeUndefined();
    await expect(access(join(extractDirectory, 'Report', 'structured', 'storage-health.json'))).resolves.toBeUndefined();
    await expect(access(join(extractDirectory, 'Report', 'lsblk.html'))).resolves.toBeUndefined();
    expect(result.analysis.files[0].issues[0].message).toBe('nvmeI/O错误');
    await expect(readFile(result.reportPath, 'utf8')).resolves.toContain('nvmeI/O错误');
  });

  it('解压 ZIP 时只使用 ZIP 规则，并读取动态文件名与 gzip 日志', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-archive-'));
    directories.push(root);
    const archivePath = join(root, 'nas.zip');
    const extractDirectory = join(root, 'nas');
    await createZip(archivePath, [
      { name: 'EC554_syslog', content: 'nvme nvme0: I/O Error: shared marker\nnvme nvme0: I/O 1 QID 0 timeout, reset controller\nXFS (dm-0): Log I/O Error Detected. Shutting down filesystem\nUPS ups0@localhost on battery\nupsmon: Communications with UPS ups0@localhost lost\nupssched: Event: upsgone\n' },
      { name: 'EC554_dmsg.log.gz', content: gzipSync('nvme nvme0: Device not ready; aborting reset\nblk_update_request: I/O error, dev nvme0n1, sector 1\n') },
      { name: 'nas_storage.log.2', content: 'append hotplug event : remove  nvme0n1p1\n' }
    ]);

    const result = await runArchiveAnalysis({
      sourcePath: archivePath,
      extractDirectory,
      rules: builtInAnalyzerRules
    });

    const issues = result.analysis.files.flatMap((file) => file.issues);
    expect(issues.map((issue) => issue.message)).toEqual([
      'NVMe 设备未就绪，重置已中止',
      'NVMe 块设备发生 I/O 错误',
      'NVMe I/O 超时后控制器重置失败',
      'XFS 日志 I/O 错误，文件系统已强制关闭',
      'UPS 已切换至电池供电',
      'UPS 通信中断触发关机事件',
      '与 UPS 的通信已丢失',
      '存储服务记录到 NVMe 移除事件'
    ]);
    expect(issues.map((issue) => issue.severity)).toEqual(['critical', 'critical', 'critical', 'critical', 'warning', 'critical', 'info', 'critical']);
    expect(issues.some((issue) => issue.message === 'nvme控制器I/O超时')).toBe(false);
    expect(result.structured.overallHealth).toBe('critical');
  });
});

async function createZip(archivePath: string, files: Array<{ name: string; content: string | Buffer }>): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const zip = new yazl.ZipFile();
    zip.outputStream
      .pipe(createWriteStream(archivePath))
      .on('close', resolve)
      .on('error', reject);
    files.forEach((file) => zip.addBuffer(Buffer.isBuffer(file.content) ? file.content : Buffer.from(file.content), file.name));
    zip.end();
  });
}
