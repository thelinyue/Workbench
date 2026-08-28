import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('嵌入应用 RPC 边界', () => {
  it('只接受来自当前 iframe 的 App Host RPC 消息', () => {
    const hostedSurfaceSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/hosted-app-surface.tsx'), 'utf8');

    expect(hostedSurfaceSource).toContain('options.event.source !== options.frameWindow');
    expect(hostedSurfaceSource).toContain("data.type !== 'workbench-app-rpc'");
    expect(hostedSurfaceSource).toContain('options.bridge.invoke(options.appId, data.method.trim(), data.payload)');
  });
});
