import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('分析中心 TypeScript 配置', () => {
  it('包含工作台报告使用的 Vite raw 资源声明', async () => {
    const config = await readFile(resolve(process.cwd(), 'apps/analysis-center/tsconfig.json'), 'utf8');

    expect(config).toContain('../../src/shared/vite-raw.d.ts');
  });
});
