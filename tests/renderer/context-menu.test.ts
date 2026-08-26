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
});
