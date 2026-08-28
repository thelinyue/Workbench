import { mkdtemp, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { loadDevelopmentAppOverride, reloadDevelopmentAppOverride } from '../../src/main/services/app-development-override';

const manifest = {
  schemaVersion: 1,
  id: 'analysis-center',
  name: '分析中心',
  description: '诊断包分析',
  publisherId: 'thelinyue',
  version: '1.1.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.2',
  runtime: { rendererEntry: 'renderer/index.html', backendEntry: 'backend/entry.js', icon: 'renderer/icon.svg' },
  capabilities: ['file.open']
};

describe('本地开发应用覆盖', () => {
  it('未打包运行时读取显式 dist 路径并校验 manifest', async () => {
    const dist = await createDist(manifest);

    await expect(loadDevelopmentAppOverride({
      isPackaged: false,
      environment: { HEPHAESTUS_DEV_APP_ID: 'analysis-center', HEPHAESTUS_DEV_APP_DIST: dist }
    })).resolves.toMatchObject({ appId: 'analysis-center', installPath: dist, manifest });
  });

  it('打包应用忽略开发覆盖环境变量', async () => {
    await expect(loadDevelopmentAppOverride({
      isPackaged: true,
      environment: { HEPHAESTUS_DEV_APP_ID: 'analysis-center', HEPHAESTUS_DEV_APP_DIST: 'D:/not-used' }
    })).resolves.toBeUndefined();
  });

  it('重新加载时读取当前 dist manifest，而不是复用启动时的缓存', async () => {
    const dist = await createDist(manifest);
    const override = await loadDevelopmentAppOverride({
      isPackaged: false,
      environment: { HEPHAESTUS_DEV_APP_ID: 'analysis-center', HEPHAESTUS_DEV_APP_DIST: dist }
    });
    await writeFile(join(dist, 'manifest.json'), JSON.stringify({ ...manifest, version: '1.1.1', runtime: { ...manifest.runtime, backendEntry: 'backend/updated-entry.js' } }), 'utf8');

    await expect(reloadDevelopmentAppOverride(override!)).resolves.toMatchObject({
      manifest: { version: '1.1.1', runtime: { backendEntry: 'backend/updated-entry.js' } }
    });
  });

  it('拒绝缺失、错误或 ID 不匹配的开发 manifest', async () => {
    const missing = join(await mkdtemp(join(tmpdir(), 'workbench-dev-app-')), 'dist');
    await expect(loadDevelopmentAppOverride({
      isPackaged: false,
      environment: { HEPHAESTUS_DEV_APP_ID: 'analysis-center', HEPHAESTUS_DEV_APP_DIST: missing }
    })).rejects.toThrow('本地开发应用目录不存在');

    const mismatched = await createDist({ ...manifest, id: 'terminal' });
    await expect(loadDevelopmentAppOverride({
      isPackaged: false,
      environment: { HEPHAESTUS_DEV_APP_ID: 'analysis-center', HEPHAESTUS_DEV_APP_DIST: mismatched }
    })).rejects.toThrow('本地开发应用 manifest 与指定应用 ID 不一致');
  });
});

async function createDist(value: object): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), 'workbench-dev-app-'));
  const dist = join(root, 'dist');
  await mkdir(dist, { recursive: true });
  await writeFile(join(dist, 'manifest.json'), JSON.stringify(value), 'utf8');
  return dist;
}
