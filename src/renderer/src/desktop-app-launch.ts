import type { AppLaunchResult } from '../../shared/app-contract';

interface AppLauncher {
  launch(appId: string): Promise<AppLaunchResult>;
}

/**
 * 启动已安装应用并按主进程返回的 presentation 决定是否创建虚拟窗口。
 * 原生窗口已由主进程打开，renderer 必须保持无额外 React 窗口的单一展示结果。
 */
export async function launchDesktopApp(appId: string, launcher: AppLauncher, showEmbedded: () => void): Promise<AppLaunchResult> {
  const result = await launcher.launch(appId);
  if (result.presentation === 'embedded') showEmbedded();
  return result;
}
