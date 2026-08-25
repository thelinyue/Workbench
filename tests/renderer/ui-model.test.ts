import { describe, expect, it } from 'vitest';
import { getBulkDeletablePackages, sortLatestPackages, type RendererDiagnosticPackage } from '../../src/renderer/src/ui-model';

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
});
