import { describe, expect, it } from 'vitest';
import { createAppWindowContextHandler } from '../../src/main/services/app-window-context-ipc';
import type { AppManifestV1 } from '../../src/shared/app-contract';

const manifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'analysis-center',
  name: '分析中心',
  description: '诊断分析',
  publisherId: 'thelinyue',
  version: '2.0.0',
  hostApiVersion: '1.0',
  minWorkbenchVersion: '0.1.6',
  runtime: { kind: 'web', rendererEntry: 'index.html', icon: 'icon.svg' },
  capabilities: []
};

describe('应用窗口上下文 IPC', () => {
  it('只根据发送方 webContents 身份解析应用和窗口键，不接受调用方应用身份', async () => {
    const handler = createAppWindowContextHandler({
      resolveWebContents: (id) => id === 41 ? { appId: 'analysis-center', windowKey: 'main' } : undefined,
      loadManifest: async (appId) => ({ manifest: { ...manifest, id: appId }, developmentOverride: false })
    });

    const context = await handler({ sender: { id: 41 } }, 'terminal');

    expect(context).toEqual({
      appId: 'analysis-center',
      windowKey: 'main',
      name: '分析中心',
      entryUrl: 'workbench-app://analysis-center/2.0.0/index.html',
      iconUrl: 'workbench-app://analysis-center/2.0.0/icon.svg',
      developmentOverride: false
    });
  });

  it('开发覆盖上下文使用 dev 资源地址', async () => {
    const handler = createAppWindowContextHandler({
      resolveWebContents: () => ({ appId: 'analysis-center', windowKey: 'main' }),
      loadManifest: async () => ({ manifest, developmentOverride: true })
    });

    await expect(handler({ sender: { id: 51 } })).resolves.toMatchObject({
      entryUrl: 'workbench-app://analysis-center/dev/index.html',
      iconUrl: 'workbench-app://analysis-center/dev/icon.svg',
      developmentOverride: true
    });
  });

  it('拒绝未绑定 AppWindowManager 身份的桌面发送方并返回中文错误', async () => {
    const handler = createAppWindowContextHandler({
      resolveWebContents: () => undefined,
      loadManifest: async () => ({ manifest, developmentOverride: false })
    });

    await expect(handler({ sender: { id: 1 } })).rejects.toThrow('当前页面不是受信任的应用窗口');
  });
});
