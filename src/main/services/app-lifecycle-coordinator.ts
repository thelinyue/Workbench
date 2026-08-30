import type { AppInstallRecord, AppManifestV1, AppRuntimeState } from '../../shared/app-contract';

export interface AppResolvedApp {
  /** 当前注册记录；调用方不得使用 renderer 自己提交的路径替换它。 */
  record: AppInstallRecord;
  installPath: string;
  dataDirectory: string;
  manifest: AppManifestV1;
}

export interface AppLifecycleRegistry {
  list(): AppInstallRecord[];
  get(appId: string): AppInstallRecord | undefined;
  upsert(record: AppInstallRecord): void;
  setEnabled(appId: string, enabled: boolean): void;
  remove(appId: string): void;
}

export interface AppLifecycleRuntime {
  start(app: AppResolvedApp): Promise<void>;
  stop(appId: string): Promise<void>;
  getState(appId: string): AppRuntimeState;
}

export interface AppLifecycleWindowManager {
  closeApp(appId: string): Promise<void>;
}

export interface AppLifecycleUninstaller {
  uninstall(appId: string, deleteData: boolean): Promise<void>;
}

export interface AppLifecycleCoordinatorOptions {
  repository: AppLifecycleRegistry;
  runtimeManager: AppLifecycleRuntime;
  windowManager: AppLifecycleWindowManager;
  uninstaller: AppLifecycleUninstaller;
  resolveApp(appId: string): Promise<AppResolvedApp>;
  seedAppIds?: ReadonlySet<string> | readonly string[];
  isSeedApp?: (appId: string) => boolean;
  logger?: Pick<Console, 'error'>;
}

/**
 * 编排应用注册状态、runtime 和原生窗口的生命周期。
 *
 * 每个 appId 使用独立 Promise 队列，因此同一应用的 enable/disable、更新和卸载不会交错；
 * 队列尾部始终吞掉前一步的拒绝，确保一次失败不会让后续操作永久跳过。不同应用不共享队列，
 * 冷启动可以并行处理。解析应用目录由调用方注入，协调器本身不依赖 Electron 或文件加载器。
 */
export class AppLifecycleCoordinator {
  private readonly queues = new Map<string, Promise<void>>();

  public constructor(private readonly options: AppLifecycleCoordinatorOptions) {}

  /** 冷启动只处理持久化为 enabled 的应用；单个应用失败会留下 enabled + broken 供应用中心展示。 */
  public async startEnabledApps(): Promise<void> {
    const enabledApps = this.options.repository.list().filter((record) => record.enabled);
    await Promise.all(enabledApps.map((record) => this.enqueue(record.id, async () => {
      try {
        await this.startResolved(record.id);
      } catch (error) {
        this.recordFailure(record.id, record, '启动应用失败', error, true);
        (this.options.logger ?? console).error(`启动应用失败（${record.id}）：${errorMessage(error)}`);
      }
    })));
  }

  /** 手动切换应用；启用失败会回滚 enabled=false，停用则必须先落库再收口副作用。 */
  public async setEnabled(appId: string, enabled: boolean): Promise<AppInstallRecord> {
    return this.enqueue(appId, async () => {
      const record = this.requireRecord(appId);
      if (record.enabled === enabled && (enabled
        ? this.options.runtimeManager.getState(appId) === 'running'
        : record.state !== 'broken')) {
        return record;
      }

      if (enabled) {
        this.options.repository.setEnabled(appId, true);
        try {
          await this.startResolved(appId);
          return this.options.repository.get(appId) ?? { ...record, enabled: true, state: 'installed', errorMessage: undefined };
        } catch (error) {
          this.recordFailure(appId, record, '启用应用失败', error, false);
          throw new Error(`启用应用失败（${appId}）：${errorMessage(error)}`, { cause: error });
        }
      }

      // disabled 必须先持久化；即使后续窗口或 runtime 收口失败，重启也不会再次启动它。
      this.options.repository.setEnabled(appId, false);
      const disabled = { ...record, enabled: false };
      try {
        const failures: string[] = [];
        try {
          await this.options.windowManager.closeApp(appId);
        } catch (error) {
          failures.push(`关闭应用窗口失败：${errorMessage(error)}`);
        }
        try {
          await this.options.runtimeManager.stop(appId);
        } catch (error) {
          failures.push(`停止应用运行时失败：${errorMessage(error)}`);
        }
        if (failures.length > 0) throw new Error(failures.join('；'));
        return this.options.repository.get(appId) ?? disabled;
      } catch (error) {
        this.recordFailure(appId, disabled, '停用应用失败', error, false);
        throw new Error(`停用应用失败（${appId}）：${errorMessage(error)}`, { cause: error });
      }
    });
  }

  /** 检查持久化启用状态并解析当前应用；RPC/启动入口可用此方法统一拒绝 disabled 应用。 */
  public runEnabled<T>(appId: string, operation: (resolvedApp: AppResolvedApp) => Promise<T>): Promise<T> {
    return this.enqueue(appId, async () => {
      const record = this.requireRecord(appId);
      if (!record.enabled) throw new Error(`应用已停用：${appId}`);
      const resolved = await this.options.resolveApp(appId);
      return operation({ ...resolved, record: this.options.repository.get(appId) ?? resolved.record });
    });
  }

  /** 安装完成后复用同一应用队列；更新时先关闭窗口、停止旧 runtime，再解析并启动新目录。 */
  public async afterInstall(appId: string, wasUpdate: boolean): Promise<void> {
    await this.enqueue(appId, async () => {
      const record = this.requireRecord(appId);
      if (!record.enabled) return;
      try {
        if (wasUpdate) {
          const failures: string[] = [];
          try {
            await this.options.windowManager.closeApp(appId);
          } catch (error) {
            failures.push(`关闭应用窗口失败：${errorMessage(error)}`);
          }
          try {
            await this.options.runtimeManager.stop(appId);
          } catch (error) {
            failures.push(`停止应用运行时失败：${errorMessage(error)}`);
          }
          if (failures.length > 0) throw new Error(failures.join('；'));
        }
        await this.startResolved(appId);
      } catch (error) {
        this.recordFailure(appId, record, '安装后启动应用失败', error, true);
        throw new Error(`安装后启动应用失败（${appId}）：${errorMessage(error)}`, { cause: error });
      }
    });
  }

  /**
   * 卸载必须在应用队列中完成，顺序为禁用、关窗、停 runtime、删文件、移除注册记录。
   * 内置种子应用在进入队列和任何副作用前直接拒绝，删除或停止失败则保留 disabled 记录。
   */
  public async uninstall(appId: string, deleteData: boolean): Promise<void> {
    if (this.isSeedApp(appId)) throw new Error(`内置种子应用不可卸载：${appId}`);
    await this.enqueue(appId, async () => {
      if (this.isSeedApp(appId)) throw new Error(`内置种子应用不可卸载：${appId}`);
      const record = this.requireRecord(appId);
      this.options.repository.setEnabled(appId, false);
      const disabled = { ...record, enabled: false };
      try {
        const failures: string[] = [];
        try {
          await this.options.windowManager.closeApp(appId);
        } catch (error) {
          failures.push(`关闭应用窗口失败：${errorMessage(error)}`);
        }
        try {
          await this.options.runtimeManager.stop(appId);
        } catch (error) {
          failures.push(`停止应用运行时失败：${errorMessage(error)}`);
        }
        if (failures.length > 0) throw new Error(failures.join('；'));
        await this.options.uninstaller.uninstall(appId, deleteData);
        this.options.repository.remove(appId);
      } catch (error) {
        this.recordFailure(appId, disabled, '卸载应用失败', error, false);
        throw new Error(`卸载应用失败（${appId}）：${errorMessage(error)}`, { cause: error });
      }
    });
  }

  private startResolved(appId: string): Promise<void> {
    return this.options.resolveApp(appId).then(async (resolved) => {
      const current = this.options.repository.get(appId);
      await this.options.runtimeManager.start({ ...resolved, record: current ?? resolved.record });
      if (current) this.options.repository.upsert({ ...current, state: 'installed', errorMessage: undefined });
    });
  }

  private isSeedApp(appId: string): boolean {
    const configured = this.options.seedAppIds;
    if (this.options.isSeedApp?.(appId)) return true;
    if (!configured) return false;
    if (Array.isArray(configured as readonly string[])) return (configured as readonly string[]).includes(appId);
    return (configured as ReadonlySet<string>).has(appId);
  }

  private requireRecord(appId: string): AppInstallRecord {
    const record = this.options.repository.get(appId);
    if (!record) throw new Error(`找不到应用：${appId}`);
    return record;
  }

  private recordFailure(appId: string, record: AppInstallRecord, prefix: string, error: unknown, retainEnabled: boolean): void {
    const message = `${prefix}：${errorMessage(error)}`;
    try {
      this.options.repository.upsert({ ...record, enabled: retainEnabled ? record.enabled : false, state: 'broken', errorMessage: message });
    } catch (persistError) {
      (this.options.logger ?? console).error(`保存应用失败状态（${appId}）失败：${errorMessage(persistError)}`);
    }
  }

  private enqueue<T>(appId: string, operation: () => Promise<T>): Promise<T> {
    const previous = this.queues.get(appId) ?? Promise.resolve();
    const result = previous.then(operation);
    const settled = result.then(() => undefined, () => undefined);
    this.queues.set(appId, settled);
    void settled.then(() => {
      if (this.queues.get(appId) === settled) this.queues.delete(appId);
    });
    return result;
  }
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
