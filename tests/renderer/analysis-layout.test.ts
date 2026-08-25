import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('分析中心列表布局', () => {
  it('移除属性检查器并让列表占据主区域', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
    const styleSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/styles.css'), 'utf8');

    expect(appSource).not.toContain('analysis-inspector');
    expect(appSource).not.toContain('activePackage');
    expect(appSource).not.toContain('analysis-sidebar');
    expect(styleSource).toMatch(/\.analysis-explorer\{[^}]*grid-template-columns:minmax\(0,1fr\)/);
    expect(styleSource).not.toContain('.analysis-inspector');
    expect(styleSource).not.toContain('.analysis-sidebar');
  });

  it('把监控目录设置迁移到分析中心独立应用并移除全局设置窗口', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
    const analysisAppSource = readFileSync(resolve(process.cwd(), 'apps/analysis-center/renderer/view.tsx'), 'utf8');

    expect(analysisAppSource).toContain('监控目录');
    expect(analysisAppSource).toContain('自动扫描间隔');
    expect(analysisAppSource).toContain("host.invoke('settings.save'");
    expect(analysisAppSource).toContain('min={1}');
    expect(appSource).not.toContain('function SettingsWindow');
    expect(appSource).not.toContain('打开设置');
    expect(appSource).not.toContain("settings: { title: '设置'");
  });

  it('在空状态中说明 ZIP 诊断包的文件名要求', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'apps/analysis-center/renderer/view.tsx'), 'utf8');

    expect(appSource).toContain('nas_server_log');
  });

  it('在独立应用中保留诊断包导入、扫描、双分析、报告和删除操作', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'apps/analysis-center/renderer/view.tsx'), 'utf8');

    expect(appSource).toContain('导入');
    expect(appSource).toContain('扫描');
    expect(appSource).toContain("scope: 'comprehensive'");
    expect(appSource).toContain("analyze(item.id, 'storage')");
    expect(appSource).toContain('打开报告');
    expect(appSource).toContain('删除');
  });
});
