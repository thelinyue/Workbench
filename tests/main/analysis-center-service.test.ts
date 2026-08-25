import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { WorkspaceRepository } from '../../src/main/data/workspace-repository';
import { AnalysisCenterService } from '../../src/main/services/analysis-center-service';

const directories: string[] = [];
afterEach(async () => { await Promise.all(directories.splice(0).map((directory) => rm(directory, { recursive: true, force: true }))); });

describe('分析中心服务', () => {
  it('手动扫描监控目录时发现 .tgz、.tgz.temp 和 .zip 诊断包', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-service-'));
    directories.push(root);
    const inbox = join(root, 'Inbox');
    await mkdir(inbox);
    await writeFile(join(inbox, 'valid.tgz'), 'content');
    await writeFile(join(inbox, 'valid.tgz.temp'), 'content');
    await writeFile(join(inbox, 'valid.zip'), 'content');
    const repository = new WorkspaceRepository(join(root, 'workbench.db'));
    repository.saveMonitorDirectories([inbox]);
    const service = new AnalysisCenterService(repository);

    const packages = await service.scanMonitorDirectories();

    expect(packages.map((item) => item.displayName).sort()).toEqual(['valid.tgz', 'valid.tgz.temp', 'valid.zip']);
    repository.close();
  });

  it('导入不支持的格式时提示包含 ZIP 支持范围', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-service-'));
    directories.push(root);
    const unsupportedPath = join(root, 'unsupported.rar');
    await writeFile(unsupportedPath, 'content');
    const repository = new WorkspaceRepository(join(root, 'workbench.db'));
    const service = new AnalysisCenterService(repository);

    try {
      await expect(service.importPackage(unsupportedPath)).rejects.toThrow('.zip');
    } finally {
      repository.close();
    }
  });
});
