import type { DesktopIconLayout } from '../main/data/workspace-repository';
import type { AppCatalogSnapshot, AppHostEvent, AppInstallRecord } from './app-contract';

export interface WorkbenchBridge {
  shell: {
    minimize(): Promise<void>;
    toggleMaximize(): Promise<void>;
    close(): Promise<void>;
  };
  desktop: {
    loadLayout(): Promise<DesktopIconLayout[]>;
    saveLayout(layout: DesktopIconLayout[]): Promise<void>;
  };
  apps: {
    list(): Promise<AppInstallRecord[]>;
    refreshCatalog(): Promise<AppInstallRecord[]>;
    install(appId: string, version?: string): Promise<AppInstallRecord>;
    launch(appId: string): Promise<void>;
    getEntryUrl(appId: string): Promise<string>;
    invoke(appId: string, method: string, payload?: unknown): Promise<unknown>;
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


