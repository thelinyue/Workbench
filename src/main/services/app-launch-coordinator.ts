import type { AppLaunchResult, AppManifestV1 } from '../../shared/app-contract';
import type { AppWindowOpenOptions } from './app-window-manager';

export interface AppLaunchCoordinatorOptions {
  appId: string;
  name: string;
  manifest: AppManifestV1;
  startRuntime(): Promise<void>;
  openAppWindow(options: AppWindowOpenOptions): void | Promise<void>;
}

export async function launchAppFromIpc(options: AppLaunchCoordinatorOptions): Promise<AppLaunchResult> {
  await options.startRuntime();
  if (options.manifest.window) {
    await options.openAppWindow({ appId: options.appId, name: options.name, window: options.manifest.window });
    return { presentation: 'app-window' };
  }
  return { presentation: 'embedded' };
}
