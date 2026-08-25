import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('桌面顶栏', () => {
  it('不渲染系统在线状态指示器', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).not.toContain('系统在线');
    expect(appSource).not.toContain('health-indicator');
    expect(appSource).not.toContain('health-dot');
  });
});
