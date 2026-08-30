import type { AppInstallRecord, AppManifestV1 } from '../../src/shared/app-contract';
import { describe, expect, it, vi } from 'vitest';
import { AppLifecycleCoordinator, type AppResolvedApp } from '../../src/main/services/app-lifecycle-coordinator';

const manifest: AppManifestV1 = {
  schemaVersion: 1,
  id: 'demo-app',
  name: '演示应用',
  description: '测试应用',
  publisherId: 'test',
  version: '1.0.0',
  hostApiVersion: '1.1',
  minWorkbenchVersion: '0.1.0',
  runtime: { kind: 'web', rendererEntry: 'renderer/index.html', icon: 'icon.svg' },
  capabilities: []
};

describe('应用生命周期协调器', () => {
  it('disabled 应用通过 runEnabled 被拒绝且不会加载应用', async () => {
    const fixture = createFixture(record('demo-app', false));

    await expect(fixture.coordinator.runEnabled('demo-app', async () => 'ok')).rejects.toThrow('应用已停用：demo-app');
    expect(fixture.events).toEqual([]);
  });

  it('手动启用失败时恢复 disabled 并保存 broken 中文错误', async () => {
    const fixture = createFixture(record('demo-app', false), {
      resolveApp: async () => { throw new Error('manifest 损坏'); }
    });

    await expect(fixture.coordinator.setEnabled('demo-app', true)).rejects.toThrow('启用应用失败（demo-app）');

    expect(fixture.registry.get('demo-app')).toMatchObject({ enabled: false, state: 'broken' });
    expect(fixture.registry.get('demo-app')?.errorMessage).toContain('manifest 损坏');
  });

  it('冷启动逐个启动 enabled 应用，单个失败不阻断其他应用且保留 enabled', async () => {
    const first = record('first-app', true);
    const second = record('second-app', true);
    const fixture = createFixture(first, {
      records: [second],
      runtimeStart: async (app) => {
        fixture.events.push(`start:${app.record.id}`);
        if (app.record.id === 'first-app') throw new Error('Worker 启动失败');
      }
    });

    await fixture.coordinator.startEnabledApps();

    expect(fixture.registry.get('first-app')).toMatchObject({ enabled: true, state: 'broken' });
    expect(fixture.registry.get('first-app')?.errorMessage).toContain('Worker 启动失败');
    expect(fixture.registry.get('second-app')).toMatchObject({ enabled: true, state: 'installed' });
    expect(fixture.events).toContain('start:first-app');
    expect(fixture.events).toContain('start:second-app');
  });

  it('停用先落库，再关闭目标窗口并停止 runtime', async () => {
    const fixture = createFixture(record('demo-app', true));

    await fixture.coordinator.setEnabled('demo-app', false);

    expect(fixture.registry.get('demo-app')?.enabled).toBe(false);
    expect(fixture.events).toEqual(['enabled:demo-app:false', 'close:demo-app', 'stop:demo-app']);
  });

  it('同一应用的 runEnabled 与停用串行，不同应用仍可并行', async () => {
    const operationGate = deferred<void>();
    const fixture = createFixture(record('demo-app', true), {
      records: [record('other-app', true)],
      operation: async (app) => {
        fixture.events.push(`operation:${app.record.id}`);
        await operationGate.promise;
        return 'done';
      }
    });

    const operation = fixture.coordinator.runEnabled('demo-app', fixture.options.operation!);
    const disable = fixture.coordinator.setEnabled('demo-app', false);
    await vi.waitFor(() => expect(fixture.events).toEqual(['resolve:demo-app', 'operation:demo-app']));

    const other = fixture.coordinator.setEnabled('other-app', false);
    await other;
    expect(fixture.events).toContain('close:other-app');
    expect(fixture.events).toContain('stop:other-app');
    expect(fixture.events).not.toContain('enabled:demo-app:false');

    operationGate.resolve();
    await expect(operation).resolves.toBe('done');
    await disable;
    expect(fixture.events.indexOf('enabled:demo-app:false')).toBeGreaterThan(fixture.events.indexOf('operation:demo-app'));
  });

  it('更新已启用应用时在同一队列中停止旧 runtime 后启动新应用', async () => {
    const fixture = createFixture(record('demo-app', true));

    await fixture.coordinator.afterInstall('demo-app', true);

    expect(fixture.events).toEqual(['stop:demo-app', 'resolve:demo-app', 'start:demo-app', 'upsert:demo-app']);
  });

  it('卸载成功时先禁用、关闭窗口、停止 runtime、删除文件，最后移除注册记录', async () => {
    const fixture = createFixture(record('demo-app', true));

    await fixture.coordinator.uninstall('demo-app', false);

    expect(fixture.events).toEqual([
      'enabled:demo-app:false', 'close:demo-app', 'stop:demo-app', 'delete:demo-app:false', 'remove:demo-app'
    ]);
    expect(fixture.registry.get('demo-app')).toBeUndefined();
  });

  it('种子应用在任何 runtime 或文件副作用前拒绝卸载', async () => {
    const fixture = createFixture(record('seed-app', true), { seedAppIds: new Set(['seed-app']) });

    await expect(fixture.coordinator.uninstall('seed-app', true)).rejects.toThrow('内置种子应用不可卸载：seed-app');
    expect(fixture.events).toEqual([]);
  });

  it('支持注入种子判断器并在进入应用队列前拒绝卸载', async () => {
    const fixture = createFixture(record('seed-app', true), { isSeedApp: (appId) => appId === 'seed-app' });

    await expect(fixture.coordinator.uninstall('seed-app', false)).rejects.toThrow('内置种子应用不可卸载：seed-app');
    expect(fixture.events).toEqual([]);
  });

  it('停止或删除失败时保留注册记录并保持 disabled', async () => {
    const fixture = createFixture(record('demo-app', true), {
      runtimeStop: async () => { throw new Error('backend 清理失败'); }
    });

    await expect(fixture.coordinator.uninstall('demo-app', true)).rejects.toThrow('卸载应用失败（demo-app）');
    expect(fixture.registry.get('demo-app')).toMatchObject({ enabled: false, state: 'broken' });
    expect(fixture.events).toEqual(['enabled:demo-app:false', 'close:demo-app', 'stop:demo-app', 'upsert:demo-app']);
  });
});

function record(id: string, enabled: boolean): AppInstallRecord {
  return {
    id,
    name: id,
    description: id,
    publisherId: 'test',
    installedVersion: '1.0.0',
    activeVersion: '1.0.0',
    installPath: `D:/apps/${id}/1.0.0`,
    enabled,
    state: 'installed'
  };
}

function createFixture(initial: AppInstallRecord, overrides: {
  records?: AppInstallRecord[];
  resolveApp?: (appId: string) => Promise<AppResolvedApp>;
  runtimeStart?: (app: AppResolvedApp) => Promise<void>;
  runtimeStop?: (appId: string) => Promise<void>;
  operation?: (app: AppResolvedApp) => Promise<unknown>;
  seedAppIds?: ReadonlySet<string> | readonly string[];
  isSeedApp?: (appId: string) => boolean;
} = {}) {
  const events: string[] = [];
  const records = new Map([initial, ...(overrides.records ?? [])].map((item) => [item.id, item]));
  const registry = {
    list: () => [...records.values()],
    get: (appId: string) => records.get(appId),
    upsert: (item: AppInstallRecord) => { records.set(item.id, { ...item }); events.push(`upsert:${item.id}`); },
    setEnabled: (appId: string, enabled: boolean) => {
      const item = records.get(appId);
      if (!item) throw new Error(`找不到应用：${appId}`);
      records.set(appId, { ...item, enabled });
      events.push(`enabled:${appId}:${String(enabled)}`);
    },
    remove: (appId: string) => { records.delete(appId); events.push(`remove:${appId}`); }
  };
  const defaultResolve = async (appId: string): Promise<AppResolvedApp> => {
    const item = records.get(appId);
    if (!item) throw new Error(`找不到应用：${appId}`);
    events.push(`resolve:${appId}`);
    return { record: item, installPath: item.installPath!, dataDirectory: `D:/apps/${appId}/data`, manifest: { ...manifest, id: appId } };
  };
  const runtime = {
    getState: (_appId: string) => 'running' as const,
    start: async (app: AppResolvedApp) => {
      await (overrides.runtimeStart ?? (async (resolved: AppResolvedApp) => { events.push(`start:${resolved.record.id}`); }))(app);
    },
    stop: async (appId: string) => { events.push(`stop:${appId}`); await (overrides.runtimeStop ?? (async () => undefined))(appId); }
  };
  const windows = { closeApp: async (appId: string) => { events.push(`close:${appId}`); } };
  const uninstaller = { uninstall: async (appId: string, deleteData: boolean) => { events.push(`delete:${appId}:${String(deleteData)}`); } };
  const options = { operation: overrides.operation };
  const coordinator = new AppLifecycleCoordinator({
    repository: registry,
    runtimeManager: runtime,
    windowManager: windows,
    uninstaller,
    resolveApp: overrides.resolveApp ?? defaultResolve,
    seedAppIds: overrides.seedAppIds,
    isSeedApp: overrides.isSeedApp,
    logger: { error: () => undefined }
  });
  return { coordinator, registry, events, options };
}

function deferred<T>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  const promise = new Promise<T>((resolvePromise) => { resolve = resolvePromise; });
  return { promise, resolve };
}
