import { LoaderCircle, RefreshCw } from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type { AppWindowContext } from '../../shared/app-contract';
import { HostedAppSurface } from './hosted-app-surface';
import { toChineseError } from './ui-model';
import { WindowControls } from './window-controls';
import { useWindowMaximizeAnimation } from './window-maximize-animation';

const WORKBENCH_ICON_URL = new URL('./assets/workbench-icon.png', import.meta.url).href;

interface AppWindowHostViewProps {
  context: AppWindowContext | null;
  error: string;
  maximized: boolean;
  reloadToken?: number;
  reloading?: boolean;
  onReload?: () => void;
  onSurfaceError?: (error: unknown) => void;
  onMinimize?: () => void;
  onMaximize?: () => void;
  onClose?: () => void;
  surfaceRef?: RefObject<HTMLElement | null>;
}

/** 将原生任务栏标题与可信应用上下文同步；加载态保持通用 Workbench 标题。 */
export function updateAppWindowDocumentTitle(context: AppWindowContext | null, documentRef: Pick<Document, 'title'> = document): void {
  documentRef.title = context?.name ?? 'Workbench';
}

/**
 * 原生应用窗口的无状态视图边界。
 * 标题栏在加载、失败和就绪状态下保持固定高度与控件位置；只有开发覆盖上下文可增加重载按钮，
 * 所有交互区域显式标记 no-drag，剩余标题栏才交给 Electron 处理窗口拖动。
 */
export function AppWindowHostView({
  context,
  error,
  maximized,
  reloadToken = 0,
  reloading = false,
  onReload = () => undefined,
  onSurfaceError = () => undefined,
  onMinimize = () => undefined,
  onMaximize = () => undefined,
  onClose = () => undefined,
  surfaceRef
}: AppWindowHostViewProps) {
  const title = context?.name ?? 'Workbench';
  const iconUrl = context?.iconUrl ?? WORKBENCH_ICON_URL;

  return <main ref={surfaceRef} className="app-window-host">
    <header className="app-window-host-titlebar">
      <div className="app-window-host-title"><img src={iconUrl} alt="" aria-hidden="true" /><strong>{title}</strong></div>
      <div className="app-window-host-actions">
        {context?.developmentOverride && <button className="development-titlebar-reload" type="button" aria-label="重新加载开发应用" title="重新加载开发应用" disabled={reloading} onClick={onReload}>{reloading ? <LoaderCircle className="spin" size={15} aria-hidden="true" /> : <RefreshCw size={15} strokeWidth={1.5} aria-hidden="true" />}</button>}
        <WindowControls title={title} variant="window" maximized={maximized} onMinimize={onMinimize} onMaximize={onMaximize} onClose={onClose} />
      </div>
    </header>
    <section className="app-window-host-content">
      {error
        ? <div className="app-window-host-feedback app-window-host-error" role="alert"><strong>应用窗口加载失败</strong><span>{error}</span></div>
        : context
          ? <HostedAppSurface key={`${context.appId}:${reloadToken}`} appId={context.appId} name={context.name} entryUrl={context.entryUrl} onError={onSurfaceError} onReady={() => void window.workbench.appWindow.markEventSurfaceReady()} />
          : <div className="app-window-host-feedback" role="status"><LoaderCircle className="spin" size={22} aria-hidden="true" /><span>正在加载应用窗口…</span></div>}
    </section>
  </main>;
}

/**
 * 原生应用窗口 renderer 根组件。
 * 上下文完全由 sender-bound IPC 获取；开发重载成功后重新读取 manifest 上下文，并只通过 key
 * 重建 HostedAppSurface，标题栏与整个 BrowserWindow 均保持原位。
 */
export function AppWindowHost() {
  const [context, setContext] = useState<AppWindowContext | null>(null);
  const [error, setError] = useState('');
  const [maximized, setMaximized] = useState<boolean | undefined>(undefined);
  const [reloadToken, setReloadToken] = useState(0);
  const [reloading, setReloading] = useState(false);
  const surfaceRef = useRef<HTMLElement>(null);

  const loadContext = useCallback(async () => {
    if (typeof window === 'undefined' || !window.workbench) throw new Error('工作台接口尚未就绪，无法读取应用窗口上下文。');
    return window.workbench.appWindow.getContext();
  }, []);

  useEffect(() => {
    let active = true;
    void loadContext()
      .then((value) => { if (active) { setContext(value); setError(''); } })
      .catch((caught) => { if (active) setError(toChineseError(caught)); });
    return () => { active = false; };
  }, [loadContext]);

  useEffect(() => updateAppWindowDocumentTitle(context), [context]);

  useEffect(() => {
    if (typeof window === 'undefined' || !window.workbench) return;
    let active = true;
    const unsubscribe = window.workbench.shell.onMaximizedChanged(setMaximized);
    void window.workbench.shell.isMaximized()
      .then((value) => { if (active) setMaximized(value); })
      .catch((caught) => { if (active) setError(toChineseError(caught)); });
    return () => { active = false; unsubscribe(); };
  }, []);

  useWindowMaximizeAnimation(surfaceRef, maximized, 'native');

  const reload = async () => {
    if (!context || !context.developmentOverride || reloading) return;
    setReloading(true);
    try {
      await window.workbench.apps.reload(context.appId);
      setContext(await loadContext());
      setReloadToken((value) => value + 1);
      setError('');
    } catch (caught) {
      setError(toChineseError(caught));
    } finally {
      setReloading(false);
    }
  };

  return <AppWindowHostView
    context={context}
    error={error}
    maximized={maximized ?? false}
    reloadToken={reloadToken}
    reloading={reloading}
    onReload={() => void reload()}
    onSurfaceError={(caught) => setError(toChineseError(caught))}
    onMinimize={() => void window.workbench.shell.minimize()}
    onMaximize={() => void window.workbench.shell.toggleMaximize()}
    onClose={() => void window.workbench.shell.close()}
    surfaceRef={surfaceRef}
  />;
}
