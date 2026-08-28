import { describe, expect, it } from 'vitest';
import { launchAppFromIpc } from '../../src/main/services/app-launch-coordinator';
import type { AppManifestV1 } from '../../src/shared/app-contract';

const baseManifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'terminal',
  name: 'SSH 终端',
  description: '终端',
  publisherId: 'thelinyue',
  version: '1.0.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.0',
  runtime: { kind: 'web', rendererEntry: 'index.html', icon: 'icon.svg' },
  capabilities: []
};

describe('应用启动 IPC 协调', () => {
  it('有 window 声明时先启动运行时，再打开原生窗口并返回 app-window', async () => {
    const calls: string[] = [];
    const manifest: AppManifestV1 = {
      ...baseManifest,
      id: 'analysis-center',
      name: '分析中心',
      window: { defaultSize: { width: 1200, height: 800 }, minSize: { width: 800, height: 560 } }
    };

    const result = await launchAppFromIpc({
      appId: manifest.id,
      name: manifest.name,
      manifest,
      startRuntime: async () => { calls.push('runtime'); },
      openAppWindow: () => { calls.push('window'); }
    });

    expect(calls).toEqual(['runtime', 'window']);
    expect(result).toEqual({ presentation: 'app-window' });
  });

  it('旧 manifest 只启动运行时并返回 embedded', async () => {
    let opened = false;

    const result = await launchAppFromIpc({
      appId: baseManifest.id,
      name: baseManifest.name,
      manifest: baseManifest,
      startRuntime: async () => undefined,
      openAppWindow: () => { opened = true; }
    });

    expect(opened).toBe(false);
    expect(result).toEqual({ presentation: 'embedded' });
  });
});
