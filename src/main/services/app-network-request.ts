import { net } from 'electron';
import type { AppHttpResponse } from './app-catalog-client';

/**
 * 应用中心统一使用 Electron Chromium 网络栈访问目录和发布资产。
 *
 * Workbench 运行在 Windows 主进程时，Electron 网络栈能够沿用系统代理、证书和网络配置，
 * 同时支持 GitHub Release 的临时资源重定向；应用包下载完成后仍由安装器执行完整性和签名校验。
 */
export async function requestAppResource(url: string, init?: RequestInit): Promise<AppHttpResponse> {
  const response = await net.fetch(url, init);
  return {
    ok: response.ok,
    status: response.status,
    text: () => response.text(),
    arrayBuffer: () => response.arrayBuffer()
  };
}
