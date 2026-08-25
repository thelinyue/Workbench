import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';

const source = await readFile(new URL('../../src/renderer/src/App.tsx', import.meta.url), 'utf8');

describe('应用中心界面', () => {
  it('提供应用中心窗口并从应用 API 加载目录状态', () => {
    expect(source).toContain('function AppCenter');
    expect(source).toContain('window.workbench.apps.list()');
    expect(source).toContain('window.workbench.apps.refreshCatalog()');
    expect(source).toContain('window.workbench.apps.install(');
    expect(source).toContain('应用中心');
  });

  it('应用窗口由 registry 中的应用 ID 打开，而不是写死分析中心组件', () => {
    expect(source).toContain("item.id === 'app-center'");
    expect(source).toContain('item.id !== \'app-center\'');
    expect(source).toContain('APP_META');
    expect(source).toContain('onOpenApp(item.id)');
  });
});
