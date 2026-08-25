import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const ipcSource = readFileSync(resolve(process.cwd(), 'src/main/ipc.ts'), 'utf8');
const preloadSource = readFileSync(resolve(process.cwd(), 'src/preload/index.ts'), 'utf8');
const bridgeSource = readFileSync(resolve(process.cwd(), 'src/shared/bridge.d.ts'), 'utf8');
const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
const backendSource = readFileSync(resolve(process.cwd(), 'apps/analysis-center/backend/entry.ts'), 'utf8');

describe('任务清理 App Host 契约', () => {
  it('通过通用 apps:invoke 路由访问独立分析中心', () => {
    expect(ipcSource).toContain("ipcMain.handle('apps:invoke'");
    expect(preloadSource).toContain("ipcRenderer.invoke('apps:invoke'");
    expect(bridgeSource).toContain('invoke(appId: string, method: string, payload?: unknown): Promise<unknown>');
    expect(appSource).toContain("window.workbench.apps.invoke(task.appId, 'tasks.clear'");
    expect(appSource).toContain("window.workbench.apps.invoke(appId, 'tasks.clear-completed'");
    expect(backendSource).toContain("case 'tasks.clear'");
    expect(backendSource).toContain("case 'tasks.clear-completed'");
  });
});
