import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('分析中心列表布局', () => {
  it('移除属性检查器并让列表占据主区域', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
    const styleSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

    expect(appSource).not.toContain('analysis-inspector');
    expect(appSource).not.toContain('activePackage');
    expect(styleSource).toMatch(/\.analysis-explorer\{[^}]*grid-template-columns:148px minmax\(0,1fr\)/);
    expect(styleSource).not.toContain('.analysis-inspector');
  });
});
