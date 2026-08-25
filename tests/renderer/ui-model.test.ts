import { describe, expect, it } from 'vitest';
import {
  areAllSelectablePackagesSelected,
  collectNewNotifications,
  getBulkDeletablePackages,
  getNextSelectablePackageSelection,
  getSelectablePackageIds,
  mergeNotifications,
  sortLatestPackages,
  type NotificationSnapshot,
  type WorkbenchNotification,
  type RendererDiagnosticPackage
} from '../../src/renderer/src/ui-model';

const packages: RendererDiagnosticPackage[] = [
  { id: 'old', displayName: 'old.tgz', sourcePath: 'C:/old.tgz', extractPath: 'C:/old', detectedAt: '2026-08-24T08:00:00.000Z', status: 'report-ready', taskIds: [], caseId: 'case-old' },
  { id: 'new', displayName: 'new.tgz', sourcePath: 'C:/new.tgz', extractPath: 'C:/new', detectedAt: '2026-08-25T08:00:00.000Z', status: 'pending', taskIds: [], caseId: 'case-new' },
  { id: 'running', displayName: 'running.tgz', sourcePath: 'C:/running.tgz', extractPath: 'C:/running', detectedAt: '2026-08-23T08:00:00.000Z', status: 'running', taskIds: [], caseId: 'case-running' },
  { id: 'failed', displayName: 'failed.tgz', sourcePath: 'C:/failed.tgz', extractPath: 'C:/failed', detectedAt: '2026-08-22T08:00:00.000Z', status: 'failed', taskIds: [], caseId: 'case-failed' }
];

describe('分析中心渲染模型', () => {
  it('最新诊断包按检测时间倒序展示', () => {
    expect(sortLatestPackages(packages).map((item) => item.id)).toEqual(['new', 'old', 'running', 'failed']);
  });

  it('批量快捷选择只包含已完成和失败项目', () => {
    expect(getBulkDeletablePackages(packages).map((item) => item.id)).toEqual(['old', 'failed']);
  });

  it('全选只包含非运行中和非排队中的项目', () => {
    const packagesWithQueued = [...packages, { ...packages[2], id: 'queued', displayName: 'queued.tgz', status: 'queued' as const }];

    expect(getSelectablePackageIds(packagesWithQueued)).toEqual(['old', 'new', 'failed']);
    expect(areAllSelectablePackagesSelected(packagesWithQueued, ['old', 'new', 'failed', 'running', 'queued'])).toBe(true);
    expect(areAllSelectablePackagesSelected(packages, ['old', 'new'])).toBe(false);
    expect(areAllSelectablePackagesSelected(packagesWithQueued.slice(2), ['running', 'queued'])).toBe(false);
  });

  it('全选按钮在部分选择时补齐选择，在全部选择时清空选择', () => {
    const busyPackages = [packages[2], { ...packages[2], id: 'queued', displayName: 'queued.tgz', status: 'queued' as const }];

    expect(getNextSelectablePackageSelection(packages, ['new'])).toEqual(['old', 'new', 'failed']);
    expect(getNextSelectablePackageSelection(packages, ['old', 'new', 'failed'])).toEqual([]);
    expect(getNextSelectablePackageSelection(busyPackages, ['running'])).toEqual([]);
  });
});

describe('工作台消息模型', () => {
  const baseline: NotificationSnapshot = {
    packages: [packages[0]],
    apps: [{ id: 'analysis-center', name: '分析中心', state: 'installed', availableVersion: '1.0.0' }]
  };

  it('首次建立快照时不把已有诊断包和应用更新当成新消息', () => {
    const next: NotificationSnapshot = {
      packages: [packages[0]],
      apps: [{ id: 'analysis-center', name: '分析中心', state: 'update-available', availableVersion: '1.1.0' }]
    };

    expect(collectNewNotifications(null, next, '2026-08-26T09:00:00.000Z')).toEqual([]);
  });

  it('只为新增诊断包和刚进入更新状态的应用生成消息', () => {
    const next: NotificationSnapshot = {
      packages: [packages[0], packages[1]],
      apps: [{ id: 'analysis-center', name: '分析中心', state: 'update-available', availableVersion: '1.1.0' }]
    };

    expect(collectNewNotifications(baseline, next, '2026-08-26T09:00:00.000Z')).toEqual([
      expect.objectContaining({
        id: 'diagnostic-package:new',
        type: 'diagnostic-package',
        title: '发现新的诊断包',
        target: { type: 'analysis-package', packageId: 'new' }
      }),
      expect.objectContaining({
        id: 'app-update:analysis-center:1.1.0',
        type: 'app-update',
        title: '应用有新版本',
        target: { type: 'app', appId: 'analysis-center' }
      })
    ]);
  });

  it('同一应用版本重复刷新不重复生成消息，版本变化时生成新消息', () => {
    const updated: NotificationSnapshot = {
      ...baseline,
      apps: [{ id: 'analysis-center', name: '分析中心', state: 'update-available', availableVersion: '1.1.0' }]
    };
    const nextVersion: NotificationSnapshot = {
      ...updated,
      apps: [{ id: 'analysis-center', name: '分析中心', state: 'update-available', availableVersion: '1.2.0' }]
    };

    expect(collectNewNotifications(updated, updated, '2026-08-26T09:00:00.000Z')).toEqual([]);
    expect(collectNewNotifications(updated, nextVersion, '2026-08-26T09:01:00.000Z')).toEqual([
      expect.objectContaining({ id: 'app-update:analysis-center:1.2.0', message: '分析中心可更新至 1.2.0' })
    ]);
  });

  it('合并消息时按稳定 ID 去重并保留最新消息在前', () => {
    const existing: WorkbenchNotification[] = [{
      id: 'diagnostic-package:new',
      type: 'diagnostic-package',
      title: '发现新的诊断包',
      message: 'new.tgz 已加入工作台',
      createdAt: '2026-08-26T08:00:00.000Z',
      read: true,
      target: { type: 'analysis-package', packageId: 'new' }
    }];
    const incoming: WorkbenchNotification[] = [{
      ...existing[0],
      createdAt: '2026-08-26T09:00:00.000Z',
      read: false
    }, {
      id: 'app-update:analysis-center:1.1.0',
      type: 'app-update',
      title: '应用有新版本',
      message: '分析中心可更新至 1.1.0',
      createdAt: '2026-08-26T08:30:00.000Z',
      read: false,
      target: { type: 'app', appId: 'analysis-center' }
    }];

    expect(mergeNotifications(existing, incoming).map((item) => [item.id, item.createdAt, item.read])).toEqual([
      ['app-update:analysis-center:1.1.0', '2026-08-26T08:30:00.000Z', false],
      ['diagnostic-package:new', '2026-08-26T08:00:00.000Z', true]
    ]);
  });
});
