import { isAbsolute, join, resolve } from 'node:path';
import { readdir, rm } from 'node:fs/promises';

const APP_ID_PATTERN = /^[a-z0-9]+(?:[.-][a-z0-9]+)*$/;

export interface AppPackageUninstallerOptions {
  /** 应用中心专属根目录；该路径只在服务创建时注入，不从 renderer 输入读取。 */
  appsRoot: string;
}

/**
 * 按用户选择清理已停止应用的安装文件。
 *
 * 卸载器只负责文件系统，不读取注册表也不停止 runtime；调用顺序由生命周期协调器保证，
 * 这样文件删除策略可以独立测试，并避免删除过程中继续有 Worker 访问应用目录。
 */
export class AppPackageUninstaller {
  private readonly appsRoot: string;

  public constructor(options: AppPackageUninstallerOptions) {
    if (!isAbsolute(options.appsRoot)) throw new Error('应用目录根路径必须是绝对路径');
    this.appsRoot = resolve(options.appsRoot);
  }

  /** 仅接受 manifest/注册表同样格式的 appId，防止路径逃逸。 */
  public async uninstall(appId: string, deleteData: boolean): Promise<void> {
    if (!APP_ID_PATTERN.test(appId)) throw new Error(`应用 ID 无效：${appId}`);
    const appRoot = join(this.appsRoot, appId);
    if (deleteData) {
      try {
        await rm(appRoot, { recursive: true, force: true });
      } catch (error) {
        throw new Error(`删除应用文件失败（${appId}）：${errorMessage(error)}`, { cause: error });
      }
      return;
    }

    let entries: Array<{ name: string }>;
    try {
      entries = await readdir(appRoot, { withFileTypes: true });
    } catch (error) {
      if (isMissingPath(error)) return;
      throw new Error(`读取应用目录失败（${appId}）：${errorMessage(error)}`, { cause: error });
    }
    try {
      await Promise.all(entries.filter((entry) => entry.name !== 'data').map((entry) =>
        rm(join(appRoot, entry.name), { recursive: true, force: true })
      ));
    } catch (error) {
      throw new Error(`删除应用文件失败（${appId}）：${errorMessage(error)}`, { cause: error });
    }
  }
}

function isMissingPath(error: unknown): boolean {
  return Boolean(error && typeof error === 'object' && (error as NodeJS.ErrnoException).code === 'ENOENT');
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
