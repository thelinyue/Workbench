import type { DiagnosticPackage, DiagnosticPackageStatus } from '../../main/domain/diagnostic-package';

/** 渲染进程使用的诊断包模型，保持与主进程领域模型一致但不拥有任何文件系统能力。 */
export type RendererDiagnosticPackage = DiagnosticPackage;

/** 最新诊断包按“检测/导入时间”倒序排列，让用户第一眼看到刚进入工作台的包。 */
export function sortLatestPackages(packages: RendererDiagnosticPackage[]): RendererDiagnosticPackage[] {
  return [...packages].sort((left, right) => Date.parse(right.detectedAt) - Date.parse(left.detectedAt));
}

/** 删除快捷选择只针对已完成或失败项，运行中、排队、等待和取消项必须由用户单独判断。 */
export function getBulkDeletablePackages(packages: RendererDiagnosticPackage[]): RendererDiagnosticPackage[] {
  return packages.filter((item) => item.status === 'report-ready' || item.status === 'failed');
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
