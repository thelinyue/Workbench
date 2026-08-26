import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('终端核心分发', () => {
  it('将包含规则独立更新能力的桌面安装包发布为 0.1.5', () => {
    const packageJson = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'));
    expect(packageJson.version).toBe('0.1.5');
  });

  it('保留 Apps 仓库独立发布职责，并把签名终端种子包放入 Workbench 安装包', () => {
    expect(existsSync(resolve(process.cwd(), 'apps/terminal/manifest.json'))).toBe(true);
    expect(existsSync(resolve(process.cwd(), 'tools/build-terminal.mjs'))).toBe(true);
    expect(existsSync(resolve(process.cwd(), '.github/workflows/terminal-release.yml'))).toBe(false);

    const packageJson = readFileSync(resolve(process.cwd(), 'package.json'), 'utf8');
    expect(packageJson).toContain('build/seed-app/terminal');
    expect(packageJson).toContain('apps/terminal');
  });

  it('安装包构建不隐式发布，由发布工作流显式创建 Release', () => {
    const packageJson = JSON.parse(readFileSync(resolve(process.cwd(), 'package.json'), 'utf8'));
    expect(packageJson.scripts['package:win']).toContain('--publish never');
  });

  it('将终端前端资源构建为相对路径，保留 workbench-app 协议中的版本目录', () => {
    execFileSync(process.execPath, ['tools/build-terminal.mjs'], { cwd: process.cwd(), stdio: 'pipe' });

    const entry = readFileSync(resolve(process.cwd(), 'apps/terminal/dist/renderer/index.html'), 'utf8');
    const manifest = JSON.parse(readFileSync(resolve(process.cwd(), 'apps/terminal/manifest.json'), 'utf8')) as { version: string };
    const entryUrl = `workbench-app://terminal/${manifest.version}/renderer/index.html`;
    const assetsUrlPrefix = `workbench-app://terminal/${manifest.version}/assets/`;
    expect(entry).toMatch(/(?:src|href)="\.\.\/assets\//);
    expect(entry).not.toMatch(/(?:src|href)="\/assets\//);

    const relativeAssets = [...entry.matchAll(/(?:src|href)="(\.\.\/assets\/[^\"]+\.(?:js|css))"/g)].map((match) => match[1]);
    expect(relativeAssets).toEqual(expect.arrayContaining([
      expect.stringMatching(/\.js$/),
      expect.stringMatching(/\.css$/)
    ]));

    for (const asset of relativeAssets) {
      expect(new URL(asset, entryUrl).href.startsWith(assetsUrlPrefix)).toBe(true);
    }
  }, 30_000);
});
