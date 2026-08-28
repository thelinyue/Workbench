import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { App } from '../../src/renderer/src/App';
import { AppWindowHost, AppWindowHostView, updateAppWindowDocumentTitle } from '../../src/renderer/src/app-window-host';
import { launchDesktopApp } from '../../src/renderer/src/desktop-app-launch';
import { forwardHostedAppEvent, HostedAppSurface, routeHostedAppMessage } from '../../src/renderer/src/hosted-app-surface';
import * as hostedAppSurfaceModule from '../../src/renderer/src/hosted-app-surface';
import { resolveRendererSurfaceElement } from '../../src/renderer/src/renderer-surface';
import { WindowControls } from '../../src/renderer/src/window-controls';
import type { AppHostEvent, AppWindowContext } from '../../src/shared/app-contract';

describe('应用窗口 renderer 表面', () => {
  const terminalOrigin = 'workbench-app://terminal';
  const analysisCenterOrigin = 'workbench-app://analysis-center';

  it('只在 surface=app-window 时选择原生应用窗口宿主', () => {
    expect(resolveRendererSurfaceElement('?surface=app-window').type).toBe(AppWindowHost);
    expect(resolveRendererSurfaceElement('?surface=desktop').type).toBe(App);
    expect(resolveRendererSurfaceElement('').type).toBe(App);
  });

  it('按 sender-bound 上下文同步原生窗口页面标题', () => {
    const documentRef = { title: '工作台' };

    updateAppWindowDocumentTitle({
      appId: 'terminal', windowKey: 'main', name: 'SSH 终端',
      entryUrl: 'workbench-app://terminal/2.0.0/index.html',
      iconUrl: 'workbench-app://terminal/2.0.0/icon.svg', developmentOverride: false
    }, documentRef);
    expect(documentRef.title).toBe('SSH 终端');
    updateAppWindowDocumentTitle(null, documentRef);
    expect(documentRef.title).toBe('Workbench');
  });

  it('iframe sandbox 保留脚本与固定来源通信，但不授予顶层导航或 popup 权限', () => {
    const markup = renderToStaticMarkup(createElement(HostedAppSurface, {
      appId: 'analysis-center',
      name: '分析中心',
      entryUrl: 'workbench-app://analysis-center/2.0.0/index.html',
      onError: () => undefined
    }));

    expect(markup).toContain('sandbox="allow-scripts allow-same-origin"');
    expect(markup).not.toContain('allow-top-navigation');
    expect(markup).not.toContain('allow-popups');
    expect(markup).not.toContain('allow-forms');
    expect(markup).not.toContain('allow-modals');
  });

  it('仅允许 SSH 终端使用密码和主机密钥确认弹窗', () => {
    const markup = renderToStaticMarkup(createElement(HostedAppSurface, {
      appId: 'terminal',
      name: 'SSH 终端',
      entryUrl: 'workbench-app://terminal/1.0.1/renderer/index.html',
      onError: () => undefined
    }));

    expect(markup).toContain('sandbox="allow-scripts allow-same-origin allow-forms allow-modals"');
    expect(markup).not.toContain('allow-top-navigation');
    expect(markup).not.toContain('allow-popups');
  });

  it('有效 RPC 只转发当前 iframe 和当前应用的请求并回传结果', async () => {
    const frameWindow = {} as Window;
    const foreignWindow = {} as Window;
    const invoke = vi.fn(async () => ({ sessions: 2 }));
    const postMessage = vi.fn();
    const bridge = { invoke, getDroppedFilePaths: vi.fn(() => []) };

    await routeHostedAppMessage({
      appId: 'terminal',
      frameWindow,
      allowedOrigin: terminalOrigin,
      event: { source: foreignWindow, origin: terminalOrigin, data: { type: 'workbench-app-rpc', appId: 'terminal', requestId: 'rpc-1', method: 'sessions.list' } },
      bridge,
      postMessage
    });
    expect(invoke).not.toHaveBeenCalled();

    await routeHostedAppMessage({
      appId: 'terminal',
      frameWindow,
      allowedOrigin: terminalOrigin,
      event: { source: frameWindow, origin: terminalOrigin, data: { type: 'workbench-app-rpc', appId: 'terminal', requestId: 'rpc-1', method: 'sessions.list', payload: { active: true } } },
      bridge,
      postMessage
    });

    expect(invoke).toHaveBeenCalledWith('terminal', 'sessions.list', { active: true });
    expect(postMessage).toHaveBeenCalledWith({
      type: 'workbench-app-rpc-response', appId: 'terminal', requestId: 'rpc-1', ok: true, result: { sessions: 2 }
    }, terminalOrigin);
  });

  it('只接受当前 iframe 为 Tab 键申请临时捕获', async () => {
    const frameWindow = {} as Window;
    const foreignWindow = {} as Window;
    const onKeyboardCapture = vi.fn();
    const route = routeHostedAppMessage as unknown as (options: Parameters<typeof routeHostedAppMessage>[0] & {
      onKeyboardCapture(key: 'Tab', enabled: boolean): void;
    }) => Promise<boolean>;
    const base = {
      appId: 'terminal', frameWindow, allowedOrigin: terminalOrigin,
      bridge: { invoke: vi.fn(), getDroppedFilePaths: vi.fn(() => []) }, postMessage: vi.fn(), onKeyboardCapture
    };

    await route({ ...base, event: { source: foreignWindow, origin: terminalOrigin, data: { type: 'workbench-app-keyboard-capture', appId: 'terminal', key: 'Tab', enabled: true } } });
    expect(onKeyboardCapture).not.toHaveBeenCalled();

    await route({ ...base, event: { source: frameWindow, origin: terminalOrigin, data: { type: 'workbench-app-keyboard-capture', appId: 'terminal', key: 'Tab', enabled: true } } });
    expect(onKeyboardCapture).toHaveBeenCalledWith('Tab', true);
  });

  it('仅在 iframe 刚失焦且已申请捕获时阻止宿主 Tab 导航并送回按键', () => {
    const forwardCapturedHostedAppKey = (hostedAppSurfaceModule as unknown as {
      forwardCapturedHostedAppKey?: (options: {
        key: string; captureTab: boolean; frameWasFocused: boolean; preventDefault(): void;
        focusFrame(): void; postMessage(message: unknown, targetOrigin: string): void; allowedOrigin: string;
      }) => boolean;
    }).forwardCapturedHostedAppKey;
    const preventDefault = vi.fn();
    const focusFrame = vi.fn();
    const postMessage = vi.fn();

    expect(forwardCapturedHostedAppKey).toBeTypeOf('function');
    expect(forwardCapturedHostedAppKey?.({
      key: 'Tab', captureTab: true, frameWasFocused: true, preventDefault, focusFrame, postMessage, allowedOrigin: terminalOrigin
    })).toBe(true);
    expect(preventDefault).toHaveBeenCalledOnce();
    expect(focusFrame).toHaveBeenCalledOnce();
    expect(postMessage).toHaveBeenCalledWith({ type: 'workbench-app-keyboard-input', key: 'Tab' }, terminalOrigin);

    expect(forwardCapturedHostedAppKey?.({
      key: 'Tab', captureTab: false, frameWasFocused: true, preventDefault, focusFrame, postMessage, allowedOrigin: terminalOrigin
    })).toBe(false);
  });

  it('同一 iframe 导航到其他 origin 后不能发起宿主 RPC', async () => {
    const frameWindow = {} as Window;
    const invoke = vi.fn();
    const postMessage = vi.fn();

    const handled = await routeHostedAppMessage({
      appId: 'terminal',
      frameWindow,
      allowedOrigin: terminalOrigin,
      event: {
        source: frameWindow,
        origin: 'https://attacker.example',
        data: { type: 'workbench-app-rpc', appId: 'terminal', requestId: 'rpc-cross-origin', method: 'sessions.list' }
      },
      bridge: { invoke, getDroppedFilePaths: vi.fn(() => []) },
      postMessage
    });

    expect(handled).toBe(false);
    expect(invoke).not.toHaveBeenCalled();
    expect(postMessage).not.toHaveBeenCalled();
  });

  it('文件拖放按 requestId 只返回有效本地路径且不调用任何应用业务方法', async () => {
    const frameWindow = {} as Window;
    const files = [{ name: 'first.tgz' }, { name: 'second.tgz' }] as File[];
    const invoke = vi.fn();
    const postMessage = vi.fn();

    await routeHostedAppMessage({
      appId: 'analysis-center',
      frameWindow,
      allowedOrigin: analysisCenterOrigin,
      event: { source: frameWindow, origin: analysisCenterOrigin, data: { type: 'workbench-app-file-drop', appId: 'analysis-center', requestId: 'drop-7', files } },
      bridge: {
        invoke,
        getDroppedFilePaths: vi.fn(() => ['D:/inbox/first.tgz', '', '   ', 'D:/inbox/second.tgz'])
      },
      postMessage
    });

    expect(postMessage).toHaveBeenCalledWith({
      type: 'workbench-app-file-drop-response',
      appId: 'analysis-center',
      requestId: 'drop-7',
      ok: true,
      paths: ['D:/inbox/first.tgz', 'D:/inbox/second.tgz']
    }, analysisCenterOrigin);
    expect(invoke).not.toHaveBeenCalled();
  });

  it('非空拖入文件全部无法解析本地路径时返回可观察的中文失败', async () => {
    const frameWindow = {} as Window;
    const files = [{ name: 'diagnostic.tgz' }] as File[];
    const postMessage = vi.fn();

    await routeHostedAppMessage({
      appId: 'analysis-center',
      frameWindow,
      allowedOrigin: analysisCenterOrigin,
      event: { source: frameWindow, origin: analysisCenterOrigin, data: { type: 'workbench-app-file-drop', appId: 'analysis-center', requestId: 'drop-no-path', files } },
      bridge: { invoke: vi.fn(), getDroppedFilePaths: vi.fn(() => []) },
      postMessage
    });

    expect(postMessage).toHaveBeenCalledWith({
      type: 'workbench-app-file-drop-response',
      appId: 'analysis-center',
      requestId: 'drop-no-path',
      ok: false,
      errorMessage: '无法读取拖入文件的本地路径，请使用系统文件管理器重新拖入本机文件。'
    }, analysisCenterOrigin);
  });

  it('文件路径解析失败时按 requestId 返回带中文语境的错误', async () => {
    const frameWindow = {} as Window;
    const postMessage = vi.fn();

    await routeHostedAppMessage({
      appId: 'terminal',
      frameWindow,
      allowedOrigin: terminalOrigin,
      event: { source: frameWindow, origin: terminalOrigin, data: { type: 'workbench-app-file-drop', appId: 'terminal', requestId: 'drop-error', files: [{}] } },
      bridge: {
        invoke: vi.fn(),
        getDroppedFilePaths: vi.fn(() => { throw new Error('not a File'); })
      },
      postMessage
    });

    expect(postMessage).toHaveBeenCalledWith({
      type: 'workbench-app-file-drop-response',
      appId: 'terminal',
      requestId: 'drop-error',
      ok: false,
      errorMessage: '无法解析拖放文件路径：not a File'
    }, terminalOrigin);
  });

  it('从开发、正式和 HTTPS 入口派生精确 origin，并用中文拒绝无效入口', () => {
    const resolveHostedAppOrigin = (hostedAppSurfaceModule as unknown as {
      resolveHostedAppOrigin?: (entryUrl: string) => string;
    }).resolveHostedAppOrigin;

    expect(resolveHostedAppOrigin).toBeTypeOf('function');
    expect(resolveHostedAppOrigin?.('workbench-app://analysis-center/dev/index.html')).toBe('workbench-app://analysis-center');
    expect(resolveHostedAppOrigin?.('workbench-app://analysis-center/2.0.0/index.html')).toBe('workbench-app://analysis-center');
    expect(resolveHostedAppOrigin?.('https://apps.example.com/v1/index.html')).toBe('https://apps.example.com');
    expect(() => resolveHostedAppOrigin?.('not a url')).toThrow('应用入口地址无效：not a url');
  });

  it('只把当前应用的宿主事件发给 iframe', () => {
    const postMessage = vi.fn();
    const event: AppHostEvent = { appId: 'terminal', event: 'sessions.changed', payload: { count: 1 } };

    forwardHostedAppEvent({ appId: 'analysis-center', event, allowedOrigin: analysisCenterOrigin, postMessage });
    expect(postMessage).not.toHaveBeenCalled();
    forwardHostedAppEvent({ appId: 'terminal', event, allowedOrigin: terminalOrigin, postMessage });

    expect(postMessage).toHaveBeenCalledWith({ type: 'workbench-app-event', event }, terminalOrigin);
  });

  it('原生 presentation 不创建虚拟窗口，embedded presentation 才创建', async () => {
    const showEmbedded = vi.fn();

    await launchDesktopApp('analysis-center', { launch: vi.fn(async () => ({ presentation: 'app-window' as const })) }, showEmbedded);
    expect(showEmbedded).not.toHaveBeenCalled();

    await launchDesktopApp('terminal', { launch: vi.fn(async () => ({ presentation: 'embedded' as const })) }, showEmbedded);
    expect(showEmbedded).toHaveBeenCalledTimes(1);
  });
});

describe('应用窗口可访问状态与控制', () => {
  const context: AppWindowContext = {
    appId: 'analysis-center',
    windowKey: 'main',
    name: '分析中心',
    entryUrl: 'workbench-app://analysis-center/2.0.0/index.html',
    iconUrl: 'workbench-app://analysis-center/2.0.0/icon.svg',
    developmentOverride: false
  };

  it('加载和错误状态保持完整窗口结构并使用可访问反馈角色', () => {
    const loading = renderToStaticMarkup(createElement(AppWindowHostView, { context: null, error: '', maximized: false }));
    expect(getByRole(loading, 'status')).toContain('正在加载应用窗口');
    expect(getByRole(loading, 'button', { name: '最小化Workbench' })).toBeTruthy();

    const failed = renderToStaticMarkup(createElement(AppWindowHostView, { context: null, error: '应用 manifest 无法读取', maximized: false }));
    expect(getByRole(failed, 'alert')).toContain('应用窗口加载失败');
  });

  it('最大化按钮暴露准确状态，开发重载只在开发覆盖时出现', () => {
    const normal = renderToStaticMarkup(createElement(AppWindowHostView, { context, error: '', maximized: false }));
    expect(getByRole(normal, 'button', { name: '最大化分析中心' })).toBeTruthy();
    expect(queryByRole(normal, 'button', { name: '重新加载开发应用' })).toBeNull();

    const development = renderToStaticMarkup(createElement(AppWindowHostView, {
      context: { ...context, developmentOverride: true }, error: '', maximized: true
    }));
    expect(getByRole(development, 'button', { name: '还原分析中心' })).toBeTruthy();
    expect(getByRole(development, 'button', { name: '重新加载开发应用' })).toBeTruthy();
  });

  it('共享 WindowControls 通过按钮角色和准确名称暴露三项原生控制', () => {
    const markup = renderToStaticMarkup(createElement(WindowControls, {
      title: '工作台', variant: 'shell', maximized: true,
      onMinimize: () => undefined, onMaximize: () => undefined, onClose: () => undefined
    }));

    expect(getByRole(markup, 'button', { name: '最小化工作台' })).toBeTruthy();
    expect(getByRole(markup, 'button', { name: '还原工作台' })).toContain('lucide-copy');
    expect(getByRole(markup, 'button', { name: '关闭工作台' })).toBeTruthy();
  });
});

function queryByRole(markup: string, role: 'button' | 'status' | 'alert', options?: { name?: string }): string | null {
  const tag = role === 'button' ? 'button' : '[a-z][a-z0-9-]*';
  const candidates = [...markup.matchAll(new RegExp(`<(${tag})\\b[^>]*?(?:role="${role}")?[^>]*>[\\s\\S]*?</\\1>`, 'gi'))]
    .map((match) => match[0]);
  return candidates.find((candidate) => {
    if (role !== 'button' && !candidate.includes(`role="${role}"`)) return false;
    return !options?.name || candidate.includes(`aria-label="${options.name}"`);
  }) ?? null;
}

function getByRole(markup: string, role: 'button' | 'status' | 'alert', options?: { name?: string }): string {
  const result = queryByRole(markup, role, options);
  if (!result) throw new Error(`未找到 role=${role}${options?.name ? ` name=${options.name}` : ''} 的元素。`);
  return result;
}
