import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('嵌入应用 RPC 边界', () => {
  it('只接受来自当前 iframe 的 App Host RPC 消息', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain('event.source !== frame.contentWindow');
    expect(appSource).toContain("event.data?.type !== 'workbench-app-rpc'");
    expect(appSource).toContain('window.workbench.apps.invoke(appId, event.data.method, event.data.payload)');
  });

  it('应用 renderer 通过消息协议请求宿主能力，不直接访问 Electron IPC', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'apps/analysis-center/renderer/host-api.ts'), 'utf8');

    expect(appSource).toContain("type: 'workbench-app-rpc'");
    expect(appSource).toContain("type: 'workbench-app-rpc-response'");
    expect(appSource).not.toContain('ipcRenderer');
  });
});
