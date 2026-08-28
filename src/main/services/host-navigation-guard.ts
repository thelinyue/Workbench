import { pathToFileURL } from 'node:url';

export interface HostNavigationEvent {
  readonly url: string;
  readonly isMainFrame: boolean;
  preventDefault(): void;
}

export interface HostNavigationWebContents {
  setWindowOpenHandler(handler: (details: { url: string }) => { action: 'deny' }): void;
  on(event: 'will-frame-navigate', listener: (event: HostNavigationEvent) => void): unknown;
}

interface WorkbenchRendererTargetOptions {
  rendererUrl?: string;
  rendererFile?: string;
  surface?: 'app-window';
}

/**
 * 为单个宿主 BrowserWindow 固定唯一可信 renderer 表面。
 * 子 frame 仍可在 workbench-app 协议内正常导航，但任何主 frame 跳转和 popup 都不能越过
 * BrowserWindow 创建时确定的 preload 身份边界。
 */
export function installHostNavigationGuard(webContents: HostNavigationWebContents, trustedRendererUrl: string): void {
  webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  webContents.on('will-frame-navigate', (event) => {
    if (event.isMainFrame && event.url !== trustedRendererUrl) event.preventDefault();
  });
}

/** 将开发 URL 或打包文件解析为可与 Electron 导航事件精确比较的绝对 URL。 */
export function resolveWorkbenchRendererUrl(options: WorkbenchRendererTargetOptions): string {
  let target: URL;
  if (options.rendererUrl) target = new URL(options.rendererUrl);
  else if (options.rendererFile) target = pathToFileURL(options.rendererFile);
  else throw new Error('缺少 Workbench renderer 加载地址。');

  if (options.surface === 'app-window') {
    target.search = '';
    target.searchParams.set('surface', 'app-window');
  }
  return target.toString();
}
