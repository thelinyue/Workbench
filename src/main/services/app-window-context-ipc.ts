import type { AppManifestV1, AppWindowContext } from '../../shared/app-contract';

interface AppWindowIdentity {
  appId: string;
  windowKey: string;
}

interface AppWindowContextHandlerOptions {
  resolveWebContents(webContentsId: number): Readonly<AppWindowIdentity> | undefined;
  loadManifest(appId: string): Promise<{ manifest: AppManifestV1; developmentOverride: boolean }>;
}

interface AppWindowIpcEvent {
  sender: { id: number };
}

/**
 * 创建只信任 IPC 发送方的应用窗口上下文处理器。
 *
 * 处理器刻意忽略 renderer 传入的任何额外参数：应用身份只能由 AppWindowManager 保存的
 * webContents 映射反查，随后才允许读取对应 manifest 并派生 renderer/icon 地址。
 */
export function createAppWindowContextHandler(options: AppWindowContextHandlerOptions) {
  return async (event: AppWindowIpcEvent, ..._ignoredRendererInputs: unknown[]): Promise<AppWindowContext> => {
    const identity = options.resolveWebContents(event.sender.id);
    if (!identity) throw new Error('当前页面不是受信任的应用窗口，无法读取应用窗口上下文。');

    const { manifest, developmentOverride } = await options.loadManifest(identity.appId);
    const versionSegment = developmentOverride ? 'dev' : manifest.version;
    return {
      ...identity,
      name: manifest.name,
      entryUrl: `workbench-app://${identity.appId}/${versionSegment}/${manifest.runtime.rendererEntry}`,
      iconUrl: `workbench-app://${identity.appId}/${versionSegment}/${manifest.runtime.icon}`,
      developmentOverride
    };
  };
}
