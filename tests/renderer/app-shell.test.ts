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

  it('返回桌面按钮使用原始 Monitor 图标而不是工作台品牌图标', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain('<Monitor size={17} />');
    expect(appSource).not.toContain('aria-label="返回桌面" onClick={() => { setWindows((current) => current.map((item) => ({ ...item, minimized: true }))); setDrawerOpen(false); }}><img className="shell-brand-icon" src={WORKBENCH_ICON_URL}');
  });

  it('桌面图标不触发原生拖放，同时保留外部文件拖入处理', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain('onDragStart={(event) => event.preventDefault()}');
    expect(appSource).toContain('<img draggable={false} className="desktop-brand-icon"');
    expect(appSource).toContain('onDrop={(event) => { event.preventDefault(); void importDroppedFiles(event.dataTransfer.files); }}');
    expect(appSource).toContain('importAnalysisCenterFiles([...files], window.workbench.apps, false)');
    expect(appSource).not.toContain("const paths = await window.workbench.apps.invoke('analysis-center', 'host.chooseFiles') as string[];");
  });

  it('只接受当前 iframe 发来的拖放请求，并通过受控桥接解析本地路径', () => {
    const hostedSurfaceSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/hosted-app-surface.tsx'), 'utf8');
    const preloadSource = readFileSync(resolve(process.cwd(), 'src/preload/index.ts'), 'utf8');

    expect(hostedSurfaceSource).toContain("data.type === 'workbench-app-file-drop'");
    expect(hostedSurfaceSource).toContain('options.event.source !== options.frameWindow');
    expect(hostedSurfaceSource).toContain("type: 'workbench-app-file-drop-response'");
    expect(preloadSource).toContain('webUtils.getPathForFile');
  });

  it('宿主 iframe 拖放不执行业务方法，桌面拖放仍保留诊断包导入', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');
    const hostedSurfaceSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/hosted-app-surface.tsx'), 'utf8');

    expect(appSource).toContain('importAnalysisCenterFiles([...files], window.workbench.apps, false)');
    expect(hostedSurfaceSource).not.toContain('packages.import');
    expect(hostedSurfaceSource).not.toContain('analysis.start');
  });

  it('只为本地开发覆盖应用提供重载按钮，并在重载后重新挂载 iframe', () => {
    const appSource = readFileSync(resolve(process.cwd(), 'src/renderer/src/App.tsx'), 'utf8');

    expect(appSource).toContain('window.workbench.apps.reload(id)');
    expect(appSource).toContain('developmentOverride');
    expect(appSource).toContain('onReload');
    expect(appSource).toContain('重新加载开发应用');
    expect(appSource).toContain('appReloadTokens[item.id]');
  });
});
