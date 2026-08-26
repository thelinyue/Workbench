import { resolve, relative, isAbsolute, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { net, protocol } from 'electron';
import { assertSafeAppArchiveEntry } from './app-package-validator';

/**
 * 在 Electron 就绪前声明应用资源协议的安全能力。
 *
 * `standard` 让浏览器按标准 URL 规则解析 iframe 内的相对资源，
 * `secure` 将该协议标记为安全上下文。权限仅限终端页面加载所需的标准、安全协议能力。
 * 这里只声明协议能力，实际文件读取仍由下方的路径校验和只读处理器负责。
 */
export function registerAppProtocolScheme(): void {
  protocol.registerSchemesAsPrivileged([
    {
      scheme: 'workbench-app',
      privileges: { standard: true, secure: true }
    }
  ]);
}

export function resolveInstalledAppFile(appsRoot: string, appId: string, version: string, relativePath: string): string {
  if (!/^[a-z0-9]+(?:[.-][a-z0-9]+)*$/.test(appId) || !/^\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?$/.test(version)) throw new Error('应用资源标识无效');
  assertSafeAppArchiveEntry(relativePath, '应用资源路径');
  if (isAbsolute(relativePath)) throw new Error(`应用资源路径不安全：${relativePath}`);
  const appRoot = resolve(appsRoot, appId, version);
  const file = resolve(appRoot, relativePath);
  const outside = relative(appRoot, file).split(/[\\/]/).includes('..');
  if (outside || (file !== appRoot && !file.startsWith(`${appRoot}${sep}`))) throw new Error(`应用资源路径不安全：${relativePath}`);
  return file;
}

/** 注册只读 workbench-app:// 协议，应用页面不能通过 URL 访问应用目录之外的文件。 */
export function registerAppResourceProtocol(appsRoot: string): () => void {
  protocol.handle('workbench-app', async (request) => {
    try {
      const url = new URL(request.url);
      const segments = decodeURIComponent(url.pathname).replace(/^\//, '').split('/');
      const version = segments.shift();
      const entry = segments.join('/');
      if (!url.hostname || !version || !entry) return new Response('应用资源地址无效', { status: 400 });
      const file = resolveInstalledAppFile(appsRoot, url.hostname, version, entry);
      return net.fetch(pathToFileURL(file).toString());
    } catch (error) {
      return new Response(error instanceof Error ? error.message : '应用资源读取失败', { status: 404 });
    }
  });
  return () => { protocol.unhandle('workbench-app'); };
}
