import {
  Activity,
  Bell,
  CheckCheck,
  CheckCircle2,
  ChevronDown,
  CircleAlert,
  ClipboardList,
  CloudDownload,
  Copy,
  Inbox,
  LayoutGrid,
  LoaderCircle,
  Menu,
  Monitor,
  Minus,
  PackageOpen,
  Play,
  RefreshCw,
  Settings,
  Square,
  Trash2,
  X
} from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { createAppWindow, getVisibleWindows, minimizeWindow, moveWindow, resizeWindow, type AppWindow } from '../window-manager';
import { DEFAULT_ICON_LAYOUT, normalizeDesktopLayout, resolveDesktopIconPoint, type DesktopAppId } from '../desktop-layout';
import type { AppInstallRecord, AppInstallState } from '../../shared/app-contract';
import {
  formatDetectedAt,
  toChineseError,
  collectNewNotifications,
  mergeNotifications,
  type NotificationSnapshot,
  type WorkbenchNotification
} from './ui-model';

const WORKBENCH_ICON_URL = new URL('./assets/workbench-icon.png', import.meta.url).href;
const WORKBENCH_WALLPAPER_URL = new URL('./assets/workbench-wallpaper.png', import.meta.url).href;

type AppId = 'app-center' | 'analysis-center' | 'terminal' | 'lvm-uncache-tool';

/**
 * 工作台品牌图标与具体应用图标分离：品牌图标表示“返回工作台”，
 * 应用图标表示当前功能，避免用户在桌面、应用库和窗口标题栏中混淆入口。
 */
const APP_ICON_URLS: Record<AppId, string> = {
  'app-center': new URL('./assets/app-center-icon.svg', import.meta.url).href,
  'analysis-center': new URL('./assets/analysis-center-icon.svg', import.meta.url).href,
  terminal: new URL('./assets/terminal-icon.svg', import.meta.url).href,
  'lvm-uncache-tool': new URL('./assets/lvm-uncache-tool-icon.svg', import.meta.url).href
};

function resolveAppIconUrl(id: string): string {
  return APP_ICON_URLS[id as AppId] ?? WORKBENCH_ICON_URL;
}

interface TaskRecord {
  id: string;
  appId?: string;
  packageId: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  createdAt: string;
  progress: number;
  message: string;
  errorMessage?: string;
}
function isTaskClearable(task: TaskRecord): boolean {
  return task.status === 'succeeded' || task.status === 'failed' || task.status === 'cancelled';
}

const APP_META: Record<AppId, { title: string; description: string }> = {
  'app-center': { title: '应用中心', description: '安装与更新工作台应用' },
  'analysis-center': { title: '分析中心', description: '诊断包与日志报告' },
  'terminal': { title: 'SSH 终端', description: '远程主机连接与运维操作' },
  'lvm-uncache-tool': { title: 'LVM 缓存清理工具', description: '清理 LVM2 VG 缓存配置' }
};

function hasWorkbenchBridge(): boolean {
  return typeof window !== 'undefined' && Boolean(window.workbench);
}

/**
 * 工作台根壳层：管理应用图标、虚拟窗口层、任务抽屉和跨窗口错误提示。
 * 所有本地能力均通过 window.workbench 进入，渲染进程本身不访问 Node 或文件系统。
 */
export function App() {
  const [windows, setWindows] = useState<AppWindow[]>([]);
  const [tasks, setTasks] = useState<TaskRecord[]>([]);
  const [registeredApps, setRegisteredApps] = useState<AppInstallRecord[]>([]);
  const [drawerOpen, setDrawerOpen] = useState(false);
  // 主窗口由 Electron 管理，渲染层只在切换调用成功后同步控制按钮图标状态。
  const [shellMaximized, setShellMaximized] = useState(false);
  const [notificationCenterOpen, setNotificationCenterOpen] = useState(false);
  const [notifications, setNotifications] = useState<WorkbenchNotification[]>([]);
  const [appLibraryOpen, setAppLibraryOpen] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [taskClearBusyId, setTaskClearBusyId] = useState<string | null>(null);
  const [taskClearAllBusy, setTaskClearAllBusy] = useState(false);
  const [taskClearDialogOpen, setTaskClearDialogOpen] = useState(false);
  const [iconLayout, setIconLayout] = useState<Record<DesktopAppId, { x: number; y: number }>>(DEFAULT_ICON_LAYOUT);
  const [desktopLayoutReady, setDesktopLayoutReady] = useState(false);
  const iconLayoutRef = useRef(iconLayout);
  const iconDragRef = useRef<{ id: DesktopAppId; offsetX: number; offsetY: number; moved: boolean } | null>(null);
  const suppressOpenRef = useRef(false);
  const notificationSnapshotRef = useRef<NotificationSnapshot | null>(null);
  const notificationRefreshQueueRef = useRef<Promise<void>>(Promise.resolve());
  const notificationSequenceRef = useRef(0);
  const analysisFrameRef = useRef<Window | null>(null);

  /** 分析中心设置属于独立应用；宿主标题栏只发出打开命令，不读取或保存其配置。 */
  const openAnalysisSettings = () => {
    if (!analysisFrameRef.current) {
      showError('分析中心尚未加载完成，请稍后重试。');
      return;
    }
    analysisFrameRef.current.postMessage({ type: 'workbench-app-command', appId: 'analysis-center', command: 'settings.open' }, '*');
  };

  useEffect(() => { iconLayoutRef.current = iconLayout; }, [iconLayout]);

  const addNotification = useCallback((notification: WorkbenchNotification) => {
    setNotifications((current) => mergeNotifications(current, [notification]));
  }, []);

  const appendSystemNotification = useCallback((type: 'notice' | 'error', title: string, message: string) => {
    addNotification({
      id: `${type}:${Date.now()}:${notificationSequenceRef.current++}`,
      type,
      title,
      message,
      createdAt: new Date().toISOString(),
      read: false
    });
  }, [addNotification]);

  const showError = useCallback((message: unknown) => {
    const translated = toChineseError(message);
    setError(translated);
    setNotice('');
    appendSystemNotification('error', '工作台操作失败', translated);
  }, [appendSystemNotification]);

  /** iframe 仅在实际卸载或加载完成时同步引用，避免壳层状态变化触发应用重新加载。 */
  const handleAnalysisFrameWindowChange = useCallback((frameWindow: Window | null) => {
    analysisFrameRef.current = frameWindow;
  }, []);

  const showNotice = useCallback((message: string) => {
    setNotice(message);
    setError('');
    appendSystemNotification('notice', '操作已完成', message);
  }, [appendSystemNotification]);

  const refreshAppRegistry = useCallback(async () => {
    try {
      if (!hasWorkbenchBridge()) return;
      setRegisteredApps(await window.workbench.apps.list());
    } catch (caught) {
      showError(caught);
    }
  }, [showError]);

  /**
   * 全局状态变化由主进程统一广播，消息中心在这里比较前后快照，避免每个应用各自维护通知逻辑。
   * 使用队列串行化刷新，防止扫描和应用目录更新同时广播时出现竞态或重复消息。
   */
  const refreshNotificationSnapshot = useCallback(async (includePackages = false) => {
    if (!hasWorkbenchBridge()) return;
    const refresh = notificationRefreshQueueRef.current.then(async () => {
      const apps = await window.workbench.apps.list();
      const analysisApp = apps.find((item) => item.id === 'analysis-center' && item.activeVersion);
      let packages: NotificationSnapshot['packages'] = [];
      if (includePackages && analysisApp) {
        try {
          const result = await window.workbench.apps.invoke('analysis-center', 'packages.list');
          if (Array.isArray(result)) packages = result as NotificationSnapshot['packages'];
        } catch {
          // 应用尚未启动时无法读取其私有诊断包列表，等应用事件或下次刷新后建立基线。
        }
      }
      const next: NotificationSnapshot = { packages, apps };
      const previous = notificationSnapshotRef.current;
      if (previous) setNotifications((current) => mergeNotifications(current, collectNewNotifications(previous, next, new Date().toISOString())));
      notificationSnapshotRef.current = next;
    });
    notificationRefreshQueueRef.current = refresh.catch(() => undefined);
    try {
      await refresh;
    } catch (caught) {
      showError(caught);
    }
  }, [showError]);

  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，请启动 Electron 主进程后重试。');
        const [savedLayout, apps] = await Promise.all([window.workbench.desktop.loadLayout(), window.workbench.apps.list()]);
        if (!active) return;
        setRegisteredApps(apps);
        const installedAppIds = apps.filter((item) => item.activeVersion).map((item) => item.id);
        const normalizedLayout = normalizeDesktopLayout(savedLayout, installedAppIds);
        const nextLayout = normalizedLayout.reduce<Record<DesktopAppId, { x: number; y: number }>>((next, item) => ({ ...next, [item.appId]: { x: item.x, y: item.y } }), {});
        setIconLayout(nextLayout);
        iconLayoutRef.current = nextLayout;
        const changed = normalizedLayout.length !== savedLayout.length || normalizedLayout.some((item, index) => {
          const saved = savedLayout[index];
          return saved?.appId !== item.appId || saved?.x !== item.x || saved?.y !== item.y;
        });
        if (changed || savedLayout.length === 0) await window.workbench.desktop.saveLayout(normalizedLayout);
        setDesktopLayoutReady(true);
        await refreshNotificationSnapshot();
      } catch (caught) {
        if (active) showError(caught);
      }
    })();
    return () => { active = false; };
  }, [refreshAppRegistry, refreshNotificationSnapshot, showError]);

  useEffect(() => {
    if (!hasWorkbenchBridge()) return;
    return window.workbench.apps.onEvent((event) => {
      if (event.appId === 'analysis-center') void refreshNotificationSnapshot(true);
      if (event.event !== 'tasks.changed' || !event.payload || typeof event.payload !== 'object') return;
      const payload = event.payload as { tasks?: TaskRecord[] };
      if (Array.isArray(payload.tasks)) setTasks(payload.tasks.map((task) => ({ ...task, appId: event.appId })));
    });
  }, [refreshNotificationSnapshot]);

  useEffect(() => hasWorkbenchBridge() ? window.workbench.onChanged(() => { void refreshAppRegistry(); void refreshNotificationSnapshot(); }) : undefined, [refreshAppRegistry, refreshNotificationSnapshot]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setTaskClearDialogOpen(false);
        setDrawerOpen(false);
        setNotificationCenterOpen(false);
        setWindows((current) => current);
      }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, []);

  const focusWindow = (id: string) => setWindows((current) => {
    const zIndex = Math.max(0, ...current.map((item) => item.zIndex)) + 1;
    return current.map((item) => item.id === id ? { ...item, minimized: false, zIndex } : item);
  });

  const loadAnalysisTasks = async () => {
    if (!hasWorkbenchBridge()) return;
    const result = await window.workbench.apps.invoke('analysis-center', 'tasks.list');
    if (Array.isArray(result)) setTasks((result as TaskRecord[]).map((task) => ({ ...task, appId: 'analysis-center' })));
  };

  const openApp = (id: string) => {
    const appMeta = APP_META[id as AppId];
    if (id !== 'app-center' && hasWorkbenchBridge() && !registeredApps.some((app) => app.id === id && app.activeVersion)) {
      setWindows((current) => createAppWindow(current, 'app-center', APP_META['app-center'].title));
      showNotice(`请先在应用中心安装${appMeta?.title ?? id}。`);
      return;
    }
    const showAppWindow = () => {
      setWindows((current) => createAppWindow(current, id, appMeta?.title ?? id));
      window.setTimeout(() => focusWindow(id), 0);
    };
    if ((id === 'analysis-center' || id === 'terminal') && hasWorkbenchBridge()) {
      // 带 backend 的应用必须在 iframe 发起首个 RPC 前启动 Worker，避免终端配置读取落入未启动运行时。
      void window.workbench.apps.launch(id)
        .then(() => {
          showAppWindow();
          if (id === 'analysis-center') void Promise.all([refreshNotificationSnapshot(true), loadAnalysisTasks()]).catch(showError);
        })
        .catch(showError);
      return;
    }
    showAppWindow();
  };

  const closeWindow = (id: string) => setWindows((current) => current.filter((item) => item.id !== id));
  const toggleMinimize = (id: string) => setWindows((current) => minimizeWindow(current, id));
  const toggleMaximize = (id: string) => setWindows((current) => current.map((item) => item.id === id ? { ...item, maximized: !item.maximized, minimized: false } : item));
  const moveVirtualWindow = (id: string, x: number, y: number) => setWindows((current) => moveWindow(current, id, Math.max(12, x), Math.max(52, y)));

  const saveIconLayout = async (nextLayout: typeof iconLayout) => {
    setIconLayout(nextLayout);
    try {
      if (hasWorkbenchBridge()) await window.workbench.desktop.saveLayout(Object.entries(nextLayout).map(([appId, point]) => ({ appId: appId as DesktopAppId, ...point })));
    } catch (caught) {
      showError(caught);
    }
  };

  /** 应用安装或状态变化后同步桌面图标，已保存的用户位置优先保留。 */
  useEffect(() => {
    if (!desktopLayoutReady) return;
    const installedAppIds = registeredApps.filter((item) => item.activeVersion).map((item) => item.id);
    const currentLayout = Object.entries(iconLayoutRef.current).map(([appId, point]) => ({ appId, ...point }));
    const normalizedLayout = normalizeDesktopLayout(currentLayout, installedAppIds);
    const nextLayout = normalizedLayout.reduce<Record<DesktopAppId, { x: number; y: number }>>((next, item) => ({ ...next, [item.appId]: { x: item.x, y: item.y } }), {});
    const changed = Object.keys(iconLayoutRef.current).length !== Object.keys(nextLayout).length || Object.entries(nextLayout).some(([appId, point]) => {
      const current = iconLayoutRef.current[appId];
      return !current || current.x !== point.x || current.y !== point.y;
    });
    if (!changed) return;
    setIconLayout(nextLayout);
    iconLayoutRef.current = nextLayout;
    if (hasWorkbenchBridge()) void window.workbench.desktop.saveLayout(normalizedLayout).catch(showError);
  }, [desktopLayoutReady, registeredApps, showError]);

  const beginIconDrag = (event: ReactPointerEvent<HTMLButtonElement>, id: DesktopAppId) => {
    const point = iconLayoutRef.current[id];
    iconDragRef.current = { id, offsetX: event.clientX - point.x, offsetY: event.clientY - point.y, moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveIcon = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const drag = iconDragRef.current;
    if (!drag) return;
    drag.moved = true;
    const occupiedPoints = Object.entries(iconLayoutRef.current)
      .filter(([appId]) => appId !== drag.id)
      .map(([, point]) => point);
    const next = {
      ...iconLayoutRef.current,
      [drag.id]: resolveDesktopIconPoint({ x: event.clientX - drag.offsetX, y: event.clientY - drag.offsetY }, occupiedPoints)
    };
    iconLayoutRef.current = next;
    setIconLayout(next);
  };

  const handleNotificationClick = (notification: WorkbenchNotification) => {
    setNotifications((current) => current.map((item) => item.id === notification.id ? { ...item, read: true } : item));
    setNotificationCenterOpen(false);
    const target = notification.target;
    if (target?.type === 'analysis-package') openApp('analysis-center');
    if (target?.type === 'app') openApp('app-center');
  };

  const unreadNotificationCount = notifications.filter((item) => !item.read).length;
  const finishIconDrag = () => {
    const drag = iconDragRef.current;
    iconDragRef.current = null;
    if (!drag?.moved) return;
    suppressOpenRef.current = true;
    void saveIconLayout(iconLayoutRef.current);
    window.setTimeout(() => { suppressOpenRef.current = false; }, 0);
  };

  const importDroppedFiles = async (files: FileList) => {
    if (!files.length) return;
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法导入诊断包。');
      const app = registeredApps.find((item) => item.id === 'analysis-center' && item.activeVersion);
      if (!app) throw new Error('请先在应用中心安装分析中心。');
      await window.workbench.apps.launch('analysis-center');
      await Promise.all([refreshNotificationSnapshot(true), loadAnalysisTasks()]);
      const paths = await window.workbench.apps.invoke('analysis-center', 'host.chooseFiles') as string[];
      for (const path of paths) await window.workbench.apps.invoke('analysis-center', 'packages.import', { sourcePath: path });
      if (paths.length) { showNotice(`已导入 ${paths.length} 个诊断包。`); openApp('analysis-center'); }
    } catch (caught) { showError(caught); }
  };

  const clearTask = async (taskId: string) => {
    setTaskClearBusyId(taskId);
    try {
      const task = tasks.find((item) => item.id === taskId);
      if (!hasWorkbenchBridge() || !task?.appId) throw new Error('任务所属应用尚未启动，无法清理任务。');
      await window.workbench.apps.invoke(task.appId, 'tasks.clear', { taskId });
      showNotice('任务已清理。');
    } catch (caught) { showError(caught); }
    finally { setTaskClearBusyId(null); }
  };

  const clearCompletedTasks = async () => {
    setTaskClearAllBusy(true);
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法清理历史任务。');
      const appIds = [...new Set(tasks.map((task) => task.appId).filter((id): id is string => Boolean(id)))];
      let count = 0;
      for (const appId of appIds) count += Number(await window.workbench.apps.invoke(appId, 'tasks.clear-completed') ?? 0);
      setTaskClearDialogOpen(false);
      showNotice(count > 0 ? `已清理 ${count} 项历史任务。` : '没有可清理的历史任务。');
    } catch (caught) { showError(caught); }
    finally { setTaskClearAllBusy(false); }
  };

  const runningCount = tasks.filter((task) => task.status === 'running' || task.status === 'queued').length;
  const clearableTaskCount = tasks.filter(isTaskClearable).length;
  // 应用库是已安装应用的快捷入口；应用中心始终保留，便于安装其他应用。
  const appLibraryIds = (Object.keys(APP_META) as AppId[]).filter((id) => id === 'app-center' || registeredApps.some((item) => item.id === id && item.activeVersion));

  return (
    <main className="desktop-shell" style={{ '--workbench-wallpaper': `url("${WORKBENCH_WALLPAPER_URL}")` } as CSSProperties} onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); void importDroppedFiles(event.dataTransfer.files); }}>
      <div className="ambient-shape ambient-shape-one" aria-hidden="true" />
      <div className="ambient-shape ambient-shape-two" aria-hidden="true" />
      <header className="topbar">
        <div className="shell-left-tools">
          <button className="shell-launcher-button" type="button" aria-label="返回桌面" onClick={() => { setWindows((current) => current.map((item) => ({ ...item, minimized: true }))); setDrawerOpen(false); }}><Monitor size={17} /></button>
          <button className="shell-launcher-button" type="button" aria-label="打开应用库" aria-expanded={appLibraryOpen} onClick={() => setAppLibraryOpen((value) => !value)}><LayoutGrid size={17} /></button>
          {windows.length > 0 && <><span className="topbar-divider shell-left-divider" aria-hidden="true" /><div className="open-app-switcher" aria-label="已打开应用">{windows.map((item) => <button key={item.id} type="button" className={`open-app-icon ${item.minimized ? 'open-app-icon-minimized' : ''}`} aria-label={`切换到${item.title}`} title={item.title} onClick={() => focusWindow(item.id)}><img className="shell-brand-icon" src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></button>)}</div></>}
          {appLibraryOpen && <div className="app-library" role="menu" aria-label="应用库">{appLibraryIds.map((id) => { const app = APP_META[id]; return <button key={id} type="button" role="menuitem" onClick={() => { openApp(id); setAppLibraryOpen(false); }}><img className="app-library-icon" src={APP_ICON_URLS[id]} alt="" aria-hidden="true" /><span>{app.title}</span></button>; })}</div>}
        </div>
        <div className="topbar-actions">
          <button className="topbar-icon-button" type="button" aria-label={`打开任务中心${runningCount ? `，${runningCount} 项进行中` : ''}`} aria-expanded={drawerOpen} onClick={() => { setNotificationCenterOpen(false); setDrawerOpen((value) => !value); }}>
            <Activity size={18} />{runningCount > 0 && <span className="notification-dot">{runningCount}</span>}
          </button>
          <button className="topbar-icon-button" type="button" aria-label={`打开消息中心${unreadNotificationCount ? `，${unreadNotificationCount} 条未读消息` : ''}`} aria-expanded={notificationCenterOpen} onClick={() => { setDrawerOpen(false); setNotificationCenterOpen((value) => !value); }}>
            <Bell size={18} />{unreadNotificationCount > 0 && <span className="notification-dot" aria-hidden="true" />}
          </button>
          <WindowControls
            title="工作台"
            variant="shell"
            maximized={shellMaximized}
            maximizeAriaLabel="最大化或还原工作台"
            onMinimize={() => void window.workbench.shell.minimize()}
            onMaximize={async () => { await window.workbench.shell.toggleMaximize(); setShellMaximized((value) => !value); }}
            onClose={() => void window.workbench.shell.close()}
          />
        </div>
      </header>

      <section className="desktop-icons" aria-label="应用入口">
        {(Object.keys(iconLayout) as DesktopAppId[]).map((id) => {
          const registered = registeredApps.find((item) => item.id === id);
          const meta = APP_META[id as AppId];
          const title = meta?.title ?? registered?.name ?? id;
          const description = meta?.description ?? registered?.description ?? '工作台应用';
          const point = iconLayout[id];
          // 桌面图标位置由 PointerEvent 控制，禁止图片触发 Chromium 原生拖动，避免误进入文件导入 drop。
          return <button key={id} className="desktop-icon" style={{ left: point.x, top: point.y }} type="button" onPointerDown={(event) => beginIconDrag(event, id)} onPointerMove={moveIcon} onPointerUp={finishIconDrag} onPointerCancel={finishIconDrag} onDragStart={(event) => event.preventDefault()} onDoubleClick={() => openApp(id)} onClick={() => { if (!suppressOpenRef.current) openApp(id); }} aria-label={`打开${title}`}>
            <span className={`desktop-icon-image desktop-icon-${id}`}><img draggable={false} className="desktop-brand-icon" src={resolveAppIconUrl(id)} alt="" aria-hidden="true" /></span>
            <span className="desktop-icon-label">{title}</span>
            <span className="desktop-icon-caption">{description}</span>
          </button>;
        })}
      </section>

      <div className="desktop-hint"><Menu size={14} /> 拖动图标整理桌面，自动吸附网格，单击打开应用</div>

      <section className="virtual-window-layer" aria-label="应用窗口">
        {getVisibleWindows(windows).map((item) => <VirtualWindow key={item.id} item={item} onClose={closeWindow} onFocus={focusWindow} onMinimize={toggleMinimize} onMaximize={toggleMaximize} onMove={moveVirtualWindow} onResize={(id, width, height) => setWindows((current) => resizeWindow(current, id, width, height))} onOpenSettings={item.id === 'analysis-center' ? openAnalysisSettings : undefined}>
          {item.id === 'app-center' && <AppCenter onOpenApp={openApp} showError={showError} showNotice={showNotice} />}
          {item.id === 'analysis-center' && <EmbeddedApp appId="analysis-center" showError={showError} onFrameWindowChange={handleAnalysisFrameWindowChange} />}
          {item.id !== 'app-center' && item.id !== 'analysis-center' && <EmbeddedApp appId={item.id} showError={showError} />}
        </VirtualWindow>)}
      </section>

      <TaskDrawer open={drawerOpen} tasks={tasks} clearableTaskCount={clearableTaskCount} clearTaskId={taskClearBusyId} clearAllBusy={taskClearAllBusy} onClose={() => setDrawerOpen(false)} onRequestClearAll={() => setTaskClearDialogOpen(true)} onClear={(taskId) => void clearTask(taskId)} onCancel={async (taskId) => {
        try {
          const task = tasks.find((item) => item.id === taskId);
          if (!hasWorkbenchBridge() || !task?.appId) throw new Error('任务所属应用尚未启动，无法取消任务。');
          await window.workbench.apps.invoke(task.appId, 'tasks.cancel', { taskId });
          showNotice('任务已取消。');
        } catch (caught) { showError(caught); }
       }} />

      <NotificationCenter open={notificationCenterOpen} notifications={notifications} unreadCount={unreadNotificationCount} onClose={() => setNotificationCenterOpen(false)} onMarkAllRead={() => setNotifications((current) => current.map((item) => ({ ...item, read: true })))} onClick={handleNotificationClick} />

      {taskClearDialogOpen && <TaskCleanupDialog count={clearableTaskCount} busy={taskClearAllBusy} onCancel={() => setTaskClearDialogOpen(false)} onConfirm={() => void clearCompletedTasks()} />}

      {(error || notice) && <div className={`toast ${error ? 'toast-error' : 'toast-success'}`} role={error ? 'alert' : 'status'}><span>{error ? <CircleAlert size={16} /> : <CheckCircle2 size={16} />}</span><span>{error || notice}</span><button type="button" aria-label="关闭提示" onClick={() => { setError(''); setNotice(''); }}><X size={14} /></button></div>}
    </main>
  );
}

interface VirtualWindowProps {
  item: AppWindow;
  onClose: (id: string) => void;
  onFocus: (id: string) => void;
  onMinimize: (id: string) => void;
  onMaximize: (id: string) => void;
  onMove: (id: string, x: number, y: number) => void;
  onResize: (id: string, width: number, height: number) => void;
  onOpenSettings?: () => void;
  children: ReactNode;
}

/** 应用内虚拟窗口，只更新 React 状态，不创建额外 Electron BrowserWindow。 */
function VirtualWindow({ item, onClose, onFocus, onMinimize, onMaximize, onMove, onResize, onOpenSettings, children }: VirtualWindowProps) {
  const dragState = useRef<{ offsetX: number; offsetY: number } | null>(null);
  const resizeState = useRef<{ startX: number; startY: number; width: number; height: number } | null>(null);
  const style: CSSProperties = item.maximized ? { zIndex: item.zIndex } : { left: item.x, top: item.y, width: item.width, height: item.height, zIndex: item.zIndex };

  const onPointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    // 控制按钮属于标题栏，但不能被拖动捕获；否则 click 会被 Pointer Capture 吞掉。
    if (item.maximized || (event.target instanceof Element && event.target.closest('button'))) return;
    dragState.current = { offsetX: event.clientX - item.x, offsetY: event.clientY - item.y };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const onPointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!dragState.current) return;
    onMove(item.id, event.clientX - dragState.current.offsetX, event.clientY - dragState.current.offsetY);
  };
  const stopDrag = () => { dragState.current = null; };
  const onResizeStart = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (item.maximized) return;
    resizeState.current = { startX: event.clientX, startY: event.clientY, width: item.width, height: item.height };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const onResizeMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const state = resizeState.current;
    if (state) onResize(item.id, state.width + event.clientX - state.startX, state.height + event.clientY - state.startY);
  };

  return <article className={`app-window ${item.maximized ? 'app-window-maximized' : ''}`} style={style} onMouseDown={() => onFocus(item.id)}>
    <div className="window-titlebar" onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={stopDrag} onPointerCancel={stopDrag}>
      <div className="window-title"><span className="window-title-icon"><img className="window-brand-icon" src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></span><strong>{item.title}</strong></div>
      {item.id === 'analysis-center' && onOpenSettings && <button className="analysis-titlebar-settings" type="button" aria-label="打开分析中心设置" title="打开分析中心设置" onClick={onOpenSettings}><Settings size={15} strokeWidth={1.5} /></button>}
      <WindowControls
        title={item.title}
        variant="window"
        maximized={item.maximized}
        onMinimize={() => onMinimize(item.id)}
        onMaximize={() => onMaximize(item.id)}
        onClose={() => onClose(item.id)}
      />
    </div>
    {!item.minimized && <div className="window-content">{children}</div>}
    {!item.maximized && !item.minimized && <div className="app-window-resizer" aria-label="调整窗口大小" onPointerDown={onResizeStart} onPointerMove={onResizeMove} onPointerUp={() => { resizeState.current = null; }} onPointerCancel={() => { resizeState.current = null; }} />}
  </article>;
}
interface WindowControlsProps {
  title: string;
  variant: 'shell' | 'window';
  maximized?: boolean;
  maximizeAriaLabel?: string;
  onMinimize: () => void;
  onMaximize: () => void;
  onClose: () => void;
}

/**
 * 统一渲染工作台与应用窗口的系统控制按钮。
 * 两种容器只在点击区域尺寸上有差异，图标、语义标签和交互状态保持一致，避免窗口之间出现两套视觉语言。
 */
function WindowControls({ title, variant, maximized = false, maximizeAriaLabel, onMinimize, onMaximize, onClose }: WindowControlsProps) {
  const buttonClassName = `window-control-button window-control-button-${variant}`;
  const resolvedMaximizeAriaLabel = maximizeAriaLabel ?? `${maximized ? '还原' : '最大化'}${title}`;

  return <div className={`window-controls ${variant === 'shell' ? 'shell-window-controls' : ''}`} aria-label={`窗口控制：${title}`}>
    <button className={buttonClassName} type="button" aria-label={`最小化${title}`} onClick={onMinimize}><Minus size={14} strokeWidth={1.5} /></button>
    <button className={buttonClassName} type="button" aria-label={resolvedMaximizeAriaLabel} onClick={onMaximize}>{maximized ? <Copy size={14} strokeWidth={1.5} /> : <Square size={14} strokeWidth={1.5} />}</button>
    <button className={buttonClassName} type="button" aria-label={`关闭${title}`} onClick={onClose}><X size={14} strokeWidth={1.5} /></button>
  </div>;
}

interface AppCenterProps {
  onOpenApp: (id: string) => void;
  showError: (error: unknown) => void;
  showNotice: (message: string) => void;
}

const APP_STATE_LABELS: Record<AppInstallState, string> = {
  'not-installed': '未安装',
  installed: '已安装',
  'update-available': '有新版本',
  incompatible: '版本不兼容',
  broken: '启动失败',
  installing: '安装中'
};

/**
 * 应用中心只依赖 App Host API 获取目录和安装状态，不把具体应用的业务逻辑写进工作台壳层。
 * 首版保留官方分析中心入口，后续应用可以仅通过目录和安装包加入此页面。
 */
function AppCenter({ onOpenApp, showError, showNotice }: AppCenterProps) {
  const [apps, setApps] = useState<AppInstallRecord[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyAppId, setBusyAppId] = useState<string | null>(null);

  const loadApps = useCallback(async () => {
    if (!hasWorkbenchBridge()) {
      setApps([]);
      setLoading(false);
      return;
    }
    try {
      setApps(await window.workbench.apps.list());
    } catch (caught) {
      showError(caught);
    } finally {
      setLoading(false);
    }
  }, [showError]);

  useEffect(() => { void loadApps(); }, [loadApps]);
  useEffect(() => {
    if (!hasWorkbenchBridge()) return;
    const unsubscribeChanged = window.workbench.onChanged(() => { void loadApps(); });
    const unsubscribeEvents = window.workbench.apps.onEvent(() => { void loadApps(); });
    return () => { unsubscribeChanged(); unsubscribeEvents(); };
  }, [loadApps]);

  const refreshCatalog = async () => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法刷新应用目录。'); return; }
    setRefreshing(true);
    try {
      setApps(await window.workbench.apps.refreshCatalog());
      showNotice('应用目录已刷新。');
    } catch (caught) {
      showError(caught);
    } finally {
      setRefreshing(false);
    }
  };

  const install = async (item: AppInstallRecord) => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法安装应用。'); return; }
    setBusyAppId(item.id);
    try {
      await window.workbench.apps.install(item.id, item.availableVersion);
      setApps(await window.workbench.apps.list());
      showNotice(`${item.name} 已安装完成。`);
    } catch (caught) {
      showError(caught);
    } finally {
      setBusyAppId(null);
    }
  };

  const launch = async (item: AppInstallRecord) => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法启动应用。'); return; }
    setBusyAppId(item.id);
    try {
      await window.workbench.apps.launch(item.id);
      onOpenApp(item.id);
    } catch (caught) {
      showError(caught);
    } finally {
      setBusyAppId(null);
    }
  };

  const renderAction = (item: AppInstallRecord) => {
    const busy = busyAppId === item.id || item.state === 'installing';
    if (item.state === 'not-installed' || item.state === 'update-available') {
      return <button type="button" className="primary-button" disabled={busy} onClick={() => void install(item)}>{busy ? <LoaderCircle className="spin" size={15} /> : <CloudDownload size={15} />}{item.state === 'not-installed' ? '安装' : '更新'}</button>;
    }
    if (item.state === 'installed') {
      return <button type="button" className="primary-button" disabled={busy} onClick={() => void launch(item)}>{busy ? <LoaderCircle className="spin" size={15} /> : <Play size={15} />}打开</button>;
    }
    return null;
  };

  return <div className="app-center-view">
    <div className="app-center-toolbar"><button type="button" className="secondary-button" disabled={refreshing} onClick={() => void refreshCatalog()}>{refreshing ? <LoaderCircle className="spin" size={15} /> : <RefreshCw size={15} />}刷新目录</button></div>
     {loading ? <div className="app-center-empty"><LoaderCircle className="spin" size={24} /><span>正在读取应用目录…</span></div> : apps.length === 0 ? <div className="app-center-empty"><PackageOpen size={28} /><strong>暂无可用应用</strong><span>请刷新目录，或检查应用目录配置。</span></div> : <div className="app-card-grid">{apps.map((item) => <article className="app-card" key={item.id}><div className="app-card-icon"><img src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></div><div className="app-card-body"><div className="app-card-title"><h2>{item.name}</h2><span className={`app-state app-state-${item.state}`}>{APP_STATE_LABELS[item.state]}</span></div><p>{item.description}</p><small>{item.activeVersion ? `当前版本 ${item.activeVersion}` : item.availableVersion ? `可安装版本 ${item.availableVersion}` : '等待目录信息'}</small>{item.errorMessage && <div className="app-card-error"><CircleAlert size={14} />{item.errorMessage}</div>}</div><div className="app-card-actions">{renderAction(item)}</div></article>)}</div>}
  </div>;
}

interface EmbeddedAppProps { appId: string; showError: (error: unknown) => void; onFrameWindowChange?: (frameWindow: Window | null) => void; }

/** 未内置到壳层的应用通过 workbench-app 协议加载自己的 renderer 资源。 */
function EmbeddedApp({ appId, showError, onFrameWindowChange }: EmbeddedAppProps) {
  const [entryUrl, setEntryUrl] = useState('');
  const frameRef = useRef<HTMLIFrameElement>(null);
  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法加载应用。');
        const url = await window.workbench.apps.getEntryUrl(appId);
        if (active) setEntryUrl(url);
      } catch (caught) { if (active) showError(caught); }
    })();
    return () => { active = false; onFrameWindowChange?.(null); };
  }, [appId, showError, onFrameWindowChange]);
  useEffect(() => {
    if (!entryUrl || !hasWorkbenchBridge()) return;
    const frame = frameRef.current;
    if (!frame) return;
    const onMessage = async (event: MessageEvent<{ type?: string; appId?: string; requestId?: string; method?: string; payload?: unknown }>) => {
      if (event.source !== frame.contentWindow || event.data?.type !== 'workbench-app-rpc' || event.data.appId !== appId || !event.data.requestId || !event.data.method) return;
      try {
        const result = await window.workbench.apps.invoke(appId, event.data.method, event.data.payload);
        frame.contentWindow?.postMessage({ type: 'workbench-app-rpc-response', appId, requestId: event.data.requestId, ok: true, result }, '*');
      } catch (caught) {
        frame.contentWindow?.postMessage({ type: 'workbench-app-rpc-response', appId, requestId: event.data.requestId, ok: false, errorMessage: toChineseError(caught) }, '*');
      }
    };
    window.addEventListener('message', onMessage);
    const unsubscribe = window.workbench.apps.onEvent((event) => {
      if (event.appId === appId) frame.contentWindow?.postMessage({ type: 'workbench-app-event', event }, '*');
    });
    return () => { window.removeEventListener('message', onMessage); unsubscribe(); };
  }, [appId, entryUrl]);
  return entryUrl ? <iframe ref={frameRef} className="embedded-app-frame" title={appId} src={entryUrl} onLoad={() => onFrameWindowChange?.(frameRef.current?.contentWindow ?? null)} /> : <div className="app-center-empty"><LoaderCircle className="spin" size={22} /><span>正在加载应用…</span></div>;
}

/** 任务中心批量清理确认框，只删除历史任务记录，不触碰诊断包和报告文件。 */
function TaskCleanupDialog({ count, busy, onCancel, onConfirm }: { count: number; busy: boolean; onCancel: () => void; onConfirm: () => void }) {
  return <div className="modal-backdrop" role="presentation"><section className="confirm-dialog task-cleanup-dialog" role="dialog" aria-modal="true" aria-labelledby="task-cleanup-dialog-title">
    <div className="dialog-icon"><Trash2 size={22} /></div><div className="dialog-heading"><span className="eyebrow danger-eyebrow">TASK HISTORY CLEANUP</span><h2 id="task-cleanup-dialog-title">确定一键清理 {count} 项历史任务？</h2><p>将删除任务记录及关联分析状态，不会删除诊断包、报告文件或报告索引。清理期间按钮将暂时禁用。</p></div>
    <div className="dialog-actions"><button type="button" className="secondary-button" disabled={busy} onClick={onCancel}>取消</button><button type="button" className="danger-button" disabled={busy} onClick={onConfirm}>{busy ? <LoaderCircle className="spin" size={15} /> : <Trash2 size={15} />}一键清理</button></div>
  </section></div>;
}

interface NotificationCenterProps {
  open: boolean;
  notifications: WorkbenchNotification[];
  unreadCount: number;
  onClose: () => void;
  onMarkAllRead: () => void;
  onClick: (notification: WorkbenchNotification) => void;
}

/**
 * 全局消息面板只负责展示、已读状态和跳转回调，消息来源由工作台根壳层统一聚合。
 * 使用按钮承载整条消息，保证键盘用户可以按与鼠标相同的路径打开对应应用。
 */
function NotificationCenter({ open, notifications, unreadCount, onClose, onMarkAllRead, onClick }: NotificationCenterProps) {
  return <aside className={`notification-center ${open ? 'notification-center-open' : ''}`} aria-label="消息中心" aria-hidden={!open}>
    <div className="drawer-header"><div><span className="eyebrow">WORKBENCH NOTIFICATIONS</span><h2>消息中心</h2></div><div className="drawer-header-actions">{unreadCount > 0 && <button type="button" className="notification-mark-read" onClick={onMarkAllRead}><CheckCheck size={14} />全部已读</button>}<button type="button" className="icon-only-button" aria-label="关闭消息中心" onClick={onClose}><X size={18} /></button></div></div>
    <div className="notification-summary"><span>{unreadCount > 0 ? `${unreadCount} 条未读消息` : '全部消息已读'}</span></div>
    <div className="notification-list">{notifications.length === 0 ? <div className="notification-empty"><Inbox size={26} /><strong>暂无消息</strong><span>新的诊断包和应用更新会显示在这里。</span></div> : notifications.map((item) => {
      const Icon = item.type === 'diagnostic-package' ? PackageOpen : item.type === 'app-update' ? CloudDownload : item.type === 'error' ? CircleAlert : Bell;
      return <button className={`notification-item ${item.read ? 'notification-item-read' : ''}`} type="button" key={item.id} onClick={() => onClick(item)}>
        <span className={`notification-item-icon notification-item-icon-${item.type}`}><Icon size={16} /></span>
        <span className="notification-item-body"><strong>{item.title}</strong><span>{item.message}</span><small>{formatDetectedAt(item.createdAt)}</small></span>
        {!item.read && <span className="notification-unread-dot" aria-label="未读" />}
      </button>;
    })}</div>
  </aside>;
}
interface TaskDrawerProps {
  open: boolean;
  tasks: TaskRecord[];
  clearableTaskCount: number;
  clearTaskId: string | null;
  clearAllBusy: boolean;
  onClose: () => void;
  onRequestClearAll: () => void;
  onClear: (taskId: string) => void;
  onCancel: (taskId: string) => void;
}

function TaskDrawer({ open, tasks, clearableTaskCount, clearTaskId, clearAllBusy, onClose, onRequestClearAll, onClear, onCancel }: TaskDrawerProps) {
  return <aside className={`task-drawer ${open ? 'task-drawer-open' : ''}`} aria-label="任务中心" aria-hidden={!open}>
    <div className="drawer-header"><div><span className="eyebrow">WORKBENCH TASKS</span><h2>任务中心</h2></div><div className="drawer-header-actions">{clearableTaskCount > 0 && <button type="button" className="task-clear-all" disabled={clearAllBusy || Boolean(clearTaskId)} onClick={onRequestClearAll} aria-label={`一键清理${clearableTaskCount}项历史任务`}>{clearAllBusy ? <LoaderCircle className="spin" size={14} /> : <Trash2 size={14} />}一键清理</button>}<button type="button" className="icon-only-button" aria-label="关闭任务中心" onClick={onClose}><X size={18} /></button></div></div>
    <div className="drawer-summary"><div><strong>{tasks.filter((task) => task.status === 'running').length}</strong><span>进行中</span></div><div><strong>{tasks.filter((task) => task.status === 'succeeded').length}</strong><span>已完成</span></div><div><strong>{tasks.filter((task) => task.status === 'failed').length}</strong><span>失败</span></div></div>
    <div className="task-list">{tasks.length === 0 ? <div className="drawer-empty"><ClipboardList size={24} /><p>暂无分析任务</p></div> : tasks.map((task) => {
      const clearable = isTaskClearable(task);
      const clearBusy = clearTaskId === task.id;
      return <div className="task-row" key={task.id}>
        <div className="task-row-icon">{task.status === 'running' ? <LoaderCircle className="spin" size={16} /> : task.status === 'succeeded' ? <CheckCircle2 size={16} /> : task.status === 'failed' ? <CircleAlert size={16} /> : <ClipboardList size={16} />}</div>
        <div className="task-row-body"><strong>{task.message || '诊断包分析任务'}</strong><span>{task.status === 'running' ? `分析进度 ${task.progress}%` : task.errorMessage || task.status}</span>{task.status === 'running' && <div className="progress-track"><span style={{ width: `${task.progress}%` }} /></div>}</div>
        {clearable ? <button type="button" className="task-clear" disabled={clearBusy || clearAllBusy} aria-label={`清理任务${task.message || task.id}`} onClick={() => onClear(task.id)}>{clearBusy ? <LoaderCircle className="spin" size={13} /> : <Trash2 size={13} />}<span>{clearBusy ? '清理中' : '清理'}</span></button> : (task.status === 'running' || task.status === 'queued') && <button type="button" className="task-cancel" aria-label="取消任务" onClick={() => onCancel(task.id)}>取消</button>}
      </div>;
    })}</div>
  </aside>;
}
