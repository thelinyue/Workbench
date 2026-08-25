import { mkdtemp, rm, writeFile, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { analyzeExtractedDirectory, type AnalyzerRuleConfig } from '../../src/main/analysis/log-analyzer';

const directories: string[] = [];
const rules: AnalyzerRuleConfig = {
  version: 'test',
  files: [{
    name: 'kern',
    category: '内核服务',
    keywords: [{
      term: 'nvme.*I/O Error',
      result: 'NVMe I/O 错误',
      regex: true,
      severity: 'critical',
      context_lines: 1,
      context_direction: 'down'
    }]
  }]
};

afterEach(async () => {
  await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true })));
});

describe('内置日志分析引擎', () => {
  it('按插件规则扫描解压目录并保留命中上下文', async () => {
    const extractDirectory = await mkdtemp(join(tmpdir(), 'workbench-analysis-'));
    directories.push(extractDirectory);
    await mkdir(join(extractDirectory, 'logs'));
    await writeFile(
      join(extractDirectory, 'logs', 'kern'),
      '第一行\nnvme I/O Error: controller failed\n第三行\n',
      'utf8'
    );

    const result = await analyzeExtractedDirectory(extractDirectory, rules);

    expect(result.files).toHaveLength(1);
    expect(result.files[0]).toMatchObject({ file: 'logs/kern', category: '内核服务' });
    expect(result.files[0].issues[0]).toMatchObject({
      keyword: 'nvme.*I/O Error',
      message: 'NVMe I/O 错误',
      severity: 'critical',
      line: 2,
      contextLines: [
        { number: 2, text: 'nvme I/O Error: controller failed', hit: true },
        { number: 3, text: '第三行', hit: false }
      ]
    });
  });

  it('不会把规则未声明的文件写入分析结果', async () => {
    const extractDirectory = await mkdtemp(join(tmpdir(), 'workbench-analysis-'));
    directories.push(extractDirectory);
    await writeFile(join(extractDirectory, 'unrelated.log'), 'nvme I/O Error', 'utf8');

    await expect(analyzeExtractedDirectory(extractDirectory, rules)).resolves.toEqual({ files: [] });
  });
});
