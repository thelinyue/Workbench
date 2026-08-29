import type { DesktopIconLayout } from '../main/data/desktop-layout-repository';
import type { AppCatalogSnapshot, AppHostEvent, AppInstallRecord, AppLaunchResult, AppWindowContext } from './app-contract';

export interface WorkbenchBridge {
  shell: {
    minimize(): Promise<void>;
    toggleMaximize(): Promise<void>;
    close(): Promise<void>;
    isMaximized(): Promise<boolean>;
    onMaximizedChanged(listener: (maximized: boolean) => void): () => void;
  };
  appWindow: {
    getContext(): Promise<AppWindowContext>;
    markEventSurfaceReady(): Promise<void>;
  };
  desktop: {
    initializeLayout(defaultLayout: DesktopIconLayout[]): Promise<DesktopIconLayout[]>;
    saveLayout(layout: DesktopIconLayout[]): Promise<void>;
  };
  apps: {
    list(): Promise<AppInstallRecord[]>;
    refreshCatalog(): Promise<AppInstallRecord[]>;
    install(appId: string, version?: string): Promise<AppInstallRecord>;
    launch(appId: string): Promise<AppLaunchResult>;
    reload(appId: string): Promise<void>;
    getEntryUrl(appId: string): Promise<string>;
    invoke(appId: string, method: string, payload?: unknown): Promise<unknown>;
    getDroppedFilePaths(files: File[]): string[];
    getCatalogSnapshot(): Promise<AppCatalogSnapshot | null>;
    onEvent(listener: (event: AppHostEvent) => void): () => void;
  };
  onChanged(listener: () => void): () => void;
}

declare global {
  interface Window {
    workbench: WorkbenchBridge;
  }
}


