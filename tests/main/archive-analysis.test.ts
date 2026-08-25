import { access, mkdtemp, readFile, rm, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import * as tar from 'tar';
import { afterEach, describe, expect, it } from 'vitest';
import { runArchiveAnalysis } from '../../src/main/analysis/archive-analysis';

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
      rules: {
        files: [{
          name: 'kern',
          category: '内核服务',
          keywords: [{ term: 'nvme.*I/O Error', result: 'NVMe I/O 错误', regex: true, severity: 'critical' }]
        }]
      }
    });

    await expect(access(result.reportPath)).resolves.toBeUndefined();
    expect(result.analysis.files[0].issues[0].message).toBe('NVMe I/O 错误');
    await expect(readFile(result.reportPath, 'utf8')).resolves.toContain('NVMe I/O 错误');
  });
});
