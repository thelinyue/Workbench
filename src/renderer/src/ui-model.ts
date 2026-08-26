import type { AppInstallRecord } from '../../shared/app-contract';

export type DiagnosticPackageStatus = 'pending' | 'queued' | 'running' | 'report-ready' | 'failed' | 'cancelled';

/**
 * 宿主只展示应用通过 RPC 返回的摘要，不再依赖分析应用的主进程领域类型。
 * 这样 Workbench 可以只维护 Host API，而分析业务类型由 Workbench-Apps 独立演进。
 */
export interface RendererDiagnosticPackage {
  id: string;
  displayName: string;
  detectedAt: string;
  status: DiagnosticPackageStatus;
  sourcePath?: string;
  extractPath?: string;
  reportPath?: string;
  taskIds?: string[];
  caseId?: string;
}

export type WorkbenchNotificationType = 'diagnostic-package' | 'app-update' | 'notice' | 'error';

export type WorkbenchNotificationTarget =
  | { type: 'analysis-package'; packageId: string }
  | { type: 'app'; appId: string };

/**
 * 工作台消息只描述渲染层需要展示和跳转的信息，不携带文件系统对象或主进程能力。
 * 消息 ID 由业务对象和版本组成，保证同一事件在多次全局刷新中不会重复出现。
 */
export interface WorkbenchNotification {
  id: string;
  type: WorkbenchNotificationType;
  title: string;
  message: string;
  createdAt: string;
  read: boolean;
  target?: WorkbenchNotificationTarget;
}

export interface NotificationSnapshot {
  packages: Pick<RendererDiagnosticPackage, 'id' | 'displayName'>[];
  apps: Pick<AppInstallRecord, 'id' | 'name' | 'state' | 'availableVersion'>[];
}

/**
 * 比较两次全局状态快照，只报告基线建立之后出现的新增诊断包和应用版本更新。
 * 首次加载返回空数组，避免把历史数据误报为新消息。
 */
export function collectNewNotifications(previous: NotificationSnapshot | null, next: NotificationSnapshot, createdAt: string): WorkbenchNotification[] {
  if (!previous) return [];

  const previousPackageIds = new Set(previous.packages.map((item) => item.id));
  const previousApps = new Map(previous.apps.map((item) => [item.id, item]));
  const notifications: WorkbenchNotification[] = next.packages
    .filter((item) => !previousPackageIds.has(item.id))
    .map((item) => ({
      id: `diagnostic-package:${item.id}`,
      type: 'diagnostic-package' as const,
      title: '发现新的诊断包',
      message: `${item.displayName} 已加入工作台`,
      createdAt,
      read: false,
      target: { type: 'analysis-package' as const, packageId: item.id }
    }));

  for (const app of next.apps) {
    if (app.state !== 'update-available') continue;
    const previousApp = previousApps.get(app.id);
    if (previousApp?.state === 'update-available' && previousApp.availableVersion === app.availableVersion) continue;
    notifications.push({
      id: `app-update:${app.id}:${app.availableVersion ?? 'latest'}`,
      type: 'app-update',
      title: '应用有新版本',
      message: `${app.name}可更新至 ${app.availableVersion ?? '最新版本'}`,
      createdAt,
      read: false,
      target: { type: 'app', appId: app.id }
    });
  }

  return notifications;
}

/** 合并消息时保留首次记录，按创建时间倒序排列，避免重复事件覆盖用户已读状态。 */
export function mergeNotifications(current: WorkbenchNotification[], incoming: WorkbenchNotification[]): WorkbenchNotification[] {
  const byId = new Map(current.map((item) => [item.id, item]));
  for (const item of incoming) if (!byId.has(item.id)) byId.set(item.id, item);
  return [...byId.values()].sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt));
}

/** 最新诊断包按“检测/导入时间”倒序排列，让用户第一眼看到刚进入工作台的包。 */
export function sortLatestPackages(packages: RendererDiagnosticPackage[]): RendererDiagnosticPackage[] {
  return [...packages].sort((left, right) => Date.parse(right.detectedAt) - Date.parse(left.detectedAt));
}

/** 删除快捷选择只针对已完成或失败项，运行中、排队、等待和取消项必须由用户单独判断。 */
export function getBulkDeletablePackages(packages: RendererDiagnosticPackage[]): RendererDiagnosticPackage[] {
  return packages.filter((item) => item.status === 'report-ready' || item.status === 'failed');
}

/** 全选只覆盖当前可操作的诊断包，避免把运行中或排队中的项目带入删除选择。 */
export function getSelectablePackageIds(packages: RendererDiagnosticPackage[]): string[] {
  return packages.filter((item) => item.status !== 'running' && item.status !== 'queued').map((item) => item.id);
}

/** 判断可操作的诊断包是否已经全部选中，忽略运行中和排队中的项目。 */
export function areAllSelectablePackagesSelected(packages: RendererDiagnosticPackage[], selectedIds: readonly string[]): boolean {
  const selectableIds = getSelectablePackageIds(packages);
  return selectableIds.length > 0 && selectableIds.every((id) => selectedIds.includes(id));
}

/** 计算全选按钮下一次点击后的选择结果，全部选中时清空，否则选中全部可操作项目。 */
export function getNextSelectablePackageSelection(packages: RendererDiagnosticPackage[], selectedIds: readonly string[]): string[] {
  return areAllSelectablePackagesSelected(packages, selectedIds) ? [] : getSelectablePackageIds(packages);
}

export const statusLabels: Record<DiagnosticPackageStatus, string> = {
  pending: '待分析',
  queued: '排队中',
  running: '分析中',
  'report-ready': '已完成',
  failed: '失败',
  cancelled: '已取消'
};

export const statusTone: Record<DiagnosticPackageStatus, 'neutral' | 'info' | 'success' | 'danger'> = {
  pending: 'neutral',
  queued: 'info',
  running: 'info',
  'report-ready': 'success',
  failed: 'danger',
  cancelled: 'neutral'
};

export function formatDetectedAt(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '时间未知';
  return new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }).format(date);
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 1024) return `${Math.max(0, bytes || 0)} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = -1;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit += 1; }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unit]}`;
}

export function toChineseError(error: unknown): string {
  if (error instanceof Error && error.message.trim()) return error.message;
  if (typeof error === 'string' && error.trim()) return error;
  return '操作失败，请稍后重试。';
}
