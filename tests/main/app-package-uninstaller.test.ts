import { mkdir, mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { describe, expect, it } from 'vitest';
import { AppPackageUninstaller } from '../../src/main/services/app-package-uninstaller';

describe('应用包卸载器', () => {
  it('保留数据时删除应用根目录下除 data 外的全部条目', async () => {
    const appsRoot = await mkdtemp(join(tmpdir(), 'workbench-uninstall-'));
    try {
      const appRoot = join(appsRoot, 'demo-app');
      await mkdir(join(appRoot, 'data'), { recursive: true });
      await mkdir(join(appRoot, '1.0.0'), { recursive: true });
      await mkdir(join(appRoot, 'cache'), { recursive: true });
      await writeFile(join(appRoot, 'data', 'keep.db'), 'keep');
      await writeFile(join(appRoot, 'cache', 'discard.tmp'), 'discard');

      await new AppPackageUninstaller({ appsRoot }).uninstall('demo-app', false);

      expect(await readdir(appRoot)).toEqual(['data']);
      expect(await readdir(join(appRoot, 'data'))).toEqual(['keep.db']);
    } finally {
      await rm(appsRoot, { recursive: true, force: true });
    }
  });

  it('删除数据时删除整个应用根目录', async () => {
    const appsRoot = await mkdtemp(join(tmpdir(), 'workbench-uninstall-'));
    try {
      const appRoot = join(appsRoot, 'demo-app');
      await mkdir(join(appRoot, 'data'), { recursive: true });
      await writeFile(join(appRoot, 'data', 'remove.db'), 'remove');

      await new AppPackageUninstaller({ appsRoot }).uninstall('demo-app', true);

      await expect(readdir(appRoot)).rejects.toMatchObject({ code: 'ENOENT' });
    } finally {
      await rm(appsRoot, { recursive: true, force: true });
    }
  });

  it('拒绝未校验的应用 ID，避免路径逃逸到 appsRoot 外部', async () => {
    const appsRoot = await mkdtemp(join(tmpdir(), 'workbench-uninstall-'));
    try {
      const uninstaller = new AppPackageUninstaller({ appsRoot });

      await expect(uninstaller.uninstall('../outside', true)).rejects.toThrow('应用 ID 无效');
      await expect(uninstaller.uninstall('Demo-App', true)).rejects.toThrow('应用 ID 无效');
    } finally {
      await rm(appsRoot, { recursive: true, force: true });
    }
  });

  it('拒绝相对 appsRoot，避免卸载目标依赖进程当前目录', () => {
    expect(() => new AppPackageUninstaller({ appsRoot: 'relative/apps' })).toThrow('绝对路径');
  });
});
