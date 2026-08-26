import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('终端独立分发', () => {
  it('以 terminal 标识保留应用源码、构建脚本和发布工作流', () => {
    expect(existsSync(resolve(process.cwd(), 'apps/terminal/manifest.json'))).toBe(true);
    expect(existsSync(resolve(process.cwd(), 'tools/build-terminal.mjs'))).toBe(true);
    expect(existsSync(resolve(process.cwd(), '.github/workflows/terminal-release.yml'))).toBe(true);
  });
});
