import type { AnalysisTaskRecord, DesktopIconLayout } from '../main/data/workspace-repository';
import type { DiagnosticPackage } from '../main/domain/diagnostic-package';

export interface DeletionPreview {
  packageCount: number;
  taskCount: number;
  sourcePaths: string[];
  extractPaths: string[];
  reportPaths: string[];
  estimatedBytes: number;
  caseCount: number;
  analysisRecordCount: number;
  reportRecordCount: number;
  confirmationToken: string;
}

export interface WorkbenchBridge {
  desktop: {
    loadLayout(): Promise<DesktopIconLayout[]>;
    saveLayout(layout: DesktopIconLayout[]): Promise<void>;
  };
  analysis: {
    list(): Promise<DiagnosticPackage[]>;
    importPackage(): Promise<DiagnosticPackage | null>;
    importDroppedFiles(files: File[]): Promise<DiagnosticPackage[]>;
    scan(): Promise<DiagnosticPackage[]>;
    start(packageId: string): Promise<void>;
    startAllPending(): Promise<{ count: number; packageNames: string[] }>;
    openReport(packageId: string): Promise<void>;
    locateSource(packageId: string): Promise<void>;
    locateExtract(packageId: string): Promise<void>;
    deletePreview(packageIds: string[]): Promise<DeletionPreview>;
    deletePackages(packageIds: string[], confirmationToken: string): Promise<void>;
  };
  tasks: {
    list(): Promise<AnalysisTaskRecord[]>;
    cancel(taskId: string): Promise<void>;
  };
  onChanged(listener: () => void): () => void;
  settings: {
    getMonitorDirectories(): Promise<string[]>;
    saveMonitorDirectories(directories: string[]): Promise<void>;
    chooseMonitorDirectory(): Promise<string | null>;
  };
}

declare global {
  interface Window {
    workbench: WorkbenchBridge;
  }
}


