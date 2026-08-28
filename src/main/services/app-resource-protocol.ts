import { realpath } from 'node:fs/promises';
import { resolve, relative, isAbsolute, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { net, protocol } from 'electron';
import { assertSafeAppArchiveEntry } from './app-package-validator';
import type { DevelopmentAppOverride } from './app-development-override';

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
  return resolveAppDirectoryFile(resolve(appsRoot, appId, version), relativePath);
}

/** 根据 URL 的版本段选择正式安装目录或唯一显式启用的本地开发目录。 */
export function resolveAppResourceFile(appsRoot: string, developmentOverride: Pick<DevelopmentAppOverride, 'appId' | 'installPath'> | undefined, appId: string, version: string, relativePath: string): string {
  if (version !== 'dev') return resolveInstalledAppFile(appsRoot, appId, version, relativePath);
  if (!developmentOverride || developmentOverride.appId !== appId) throw new Error('本地开发应用资源地址无效');
  return resolveAppDirectoryFile(developmentOverride.installPath, relativePath);
}

/**
 * 读取资源前解析真实路径，防止应用目录中的符号链接指向 dist 之外的文件。
 * 真实路径校验同时覆盖正式安装目录和开发目录，协议处理器只能读取应用自己的实际文件树。
 */
export async function resolveAppResourceFileForRead(appsRoot: string, developmentOverride: Pick<DevelopmentAppOverride, 'appId' | 'installPath'> | undefined, appId: string, version: string, relativePath: string, realpathImpl: (path: string) => Promise<string> = realpath): Promise<string> {
  const file = resolveAppResourceFile(appsRoot, developmentOverride, appId, version, relativePath);
  const root = version === 'dev' ? developmentOverride!.installPath : resolve(appsRoot, appId, version);
  let realRoot: string;
  let realFile: string;
  try {
    [realRoot, realFile] = await Promise.all([realpathImpl(root), realpathImpl(file)]);
  } catch (error) {
    throw new Error(`无法读取应用资源：${error instanceof Error ? error.message : String(error)}`);
  }
  const outside = relative(realRoot, realFile).split(/[\\/]/).includes('..');
  if (outside || (realFile !== realRoot && !realFile.startsWith(`${realRoot}${sep}`))) throw new Error(`应用资源路径不安全：${relativePath}`);
  return realFile;
}

function resolveAppDirectoryFile(appRoot: string, relativePath: string): string {
  assertSafeAppArchiveEntry(relativePath, '应用资源路径');
  if (isAbsolute(relativePath)) throw new Error(`应用资源路径不安全：${relativePath}`);
  const root = resolve(appRoot);
  const file = resolve(root, relativePath);
  const outside = relative(root, file).split(/[\\/]/).includes('..');
  if (outside || (file !== root && !file.startsWith(`${root}${sep}`))) throw new Error(`应用资源路径不安全：${relativePath}`);
  return file;
}

/** 注册只读 workbench-app:// 协议，应用页面不能通过 URL 访问应用目录之外的文件。 */
export function registerAppResourceProtocol(appsRoot: string, developmentOverride?: Pick<DevelopmentAppOverride, 'appId' | 'installPath'>, isDevelopmentOverrideEnabled: () => boolean = () => Boolean(developmentOverride)): () => void {
  protocol.handle('workbench-app', async (request) => {
    try {
      const url = new URL(request.url);
      const segments = decodeURIComponent(url.pathname).replace(/^\//, '').split('/');
      const version = segments.shift();
      const entry = segments.join('/');
      if (!url.hostname || !version || !entry) return new Response('应用资源地址无效', { status: 400 });
      if (version === 'dev' && !isDevelopmentOverrideEnabled()) throw new Error('本地开发应用资源地址无效');
      const file = await resolveAppResourceFileForRead(appsRoot, developmentOverride, url.hostname, version, entry);
      return net.fetch(pathToFileURL(file).toString());
    } catch (error) {
      return new Response(error instanceof Error ? error.message : '应用资源读取失败', { status: 404 });
    }
  });
  return () => { protocol.unhandle('workbench-app'); };
}
