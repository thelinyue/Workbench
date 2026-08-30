import {
  Activity,
  Bell,
  CheckCheck,
  CheckCircle2,
  ChevronDown,
  CircleAlert,
  ClipboardList,
  CloudDownload,
  Inbox,
  LayoutGrid,
  LoaderCircle,
  Menu,
  Monitor,
  PackageOpen,
  Play,
  RefreshCw,
  Trash2,
  X
} from 'lucide-react';
import { useCallback, useEffect, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { createAppWindow, getVisibleWindows, minimizeWindow, moveWindow, resizeWindow, type AppWindow } from '../window-manager';
import { DEFAULT_ICON_LAYOUT, getDefaultIconLayout, normalizeDesktopLayout, resolveDesktopIconDropLayout, type DesktopAppId } from '../desktop-layout';
import { importAnalysisCenterFiles } from './analysis-center-file-import';
import { launchDesktopApp } from './desktop-app-launch';
import { HostedAppSurface } from './hosted-app-surface';
import { WindowControls } from './window-controls';
import { useWindowMaximizeAnimation } from './window-maximize-animation';
import { beginAppOperation, completeAppOperation, isAppOperationBusy } from '../app-operation-state';
import type { AppCenterItem } from '../../shared/bridge';
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
  const [registeredApps, setRegisteredApps] = useState<AppCenterItem[]>([]);
  const [appReloadTokens, setAppReloadTokens] = useState<Record<string, number>>({});
  const [drawerOpen, setDrawerOpen] = useState(false);
  // 主窗口状态由 Electron 原生事件同步，覆盖按钮、双击标题栏和系统快捷键等全部路径。
  const [shellMaximized, setShellMaximized] = useState<boolean | undefined>(undefined);
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
  const desktopShellRef = useRef<HTMLElement>(null);
  const desktopIconsRef = useRef<HTMLElement>(null);
  const iconDragRef = useRef<{ id: DesktopAppId; moved: boolean; initialLayout: typeof iconLayout } | null>(null);
  const suppressOpenRef = useRef(false);
  const notificationSnapshotRef = useRef<NotificationSnapshot | null>(null);
  const notificationRefreshQueueRef = useRef<Promise<void>>(Promise.resolve());
  const notificationSequenceRef = useRef(0);

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

  const showNotice = useCallback((message: string) => {
    setNotice(message);
    setError('');
    appendSystemNotification('notice', '操作已完成', message);
  }, [appendSystemNotification]);

  /**
   * 全局状态变化由主进程统一广播，消息中心在这里比较前后快照，避免每个应用各自维护通知逻辑。
   * 使用队列串行化刷新，防止扫描和应用目录更新同时广播时出现竞态或重复消息。
   */
  const refreshNotificationSnapshot = useCallback(async (includePackages = false) => {
    if (!hasWorkbenchBridge()) return;
    const refresh = notificationRefreshQueueRef.current.then(async () => {
      const apps = await window.workbench.apps.list();
      const analysisApp = apps.find((item) => item.id === 'analysis-center' && item.activeVersion && item.enabled);
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
        const apps = await window.workbench.apps.list();
        if (!active) return;
        setRegisteredApps(apps);
        const installedAppIds = apps.filter((item) => item.activeVersion && item.enabled).map((item) => item.id);
        const defaults = Object.entries(getDefaultIconLayout(installedAppIds)).map(([appId, point]) => ({ appId, ...point }));
        const savedLayout = await window.workbench.desktop.initializeLayout(defaults);
        if (!active) return;
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
  }, [refreshNotificationSnapshot, showError]);

  useEffect(() => {
    if (!hasWorkbenchBridge()) return;
    return window.workbench.apps.onEvent((event) => {
      if (event.appId === 'analysis-center') void refreshNotificationSnapshot(true);
      if (event.event !== 'tasks.changed' || !event.payload || typeof event.payload !== 'object') return;
      const payload = event.payload as { tasks?: TaskRecord[] };
      if (Array.isArray(payload.tasks)) setTasks(payload.tasks.map((task) => ({ ...task, appId: event.appId })));
    });
  }, [refreshNotificationSnapshot]);

  /**
   * 主进程广播是启停和卸载的唯一同步信号：先刷新 registry，再关闭不再可用的虚拟窗口，
   * 最后以启用应用重新归一化并持久化桌面布局，避免停用入口残留或恢复旧坐标。
   */
  const synchronizeAppState = useCallback(async () => {
    try {
      if (!hasWorkbenchBridge()) return;
      const apps = await window.workbench.apps.list();
      setRegisteredApps(apps);
      const enabledAppIds = apps.filter((item) => item.activeVersion && item.enabled).map((item) => item.id);
      setWindows((current) => current.filter((item) => item.id === 'app-center' || enabledAppIds.includes(item.id)));
      if (!desktopLayoutReady) return;
      const currentLayout = Object.entries(iconLayoutRef.current).map(([appId, point]) => ({ appId, ...point }));
      const normalizedLayout = normalizeDesktopLayout(currentLayout, enabledAppIds);
      const nextLayout = normalizedLayout.reduce<Record<DesktopAppId, { x: number; y: number }>>((next, item) => ({ ...next, [item.appId]: { x: item.x, y: item.y } }), {});
      const changed = Object.keys(iconLayoutRef.current).length !== Object.keys(nextLayout).length || Object.entries(nextLayout).some(([appId, point]) => {
        const current = iconLayoutRef.current[appId];
        return !current || current.x !== point.x || current.y !== point.y;
      });
      if (!changed) return;
      setIconLayout(nextLayout);
      iconLayoutRef.current = nextLayout;
      await window.workbench.desktop.saveLayout(normalizedLayout);
    } catch (caught) {
      showError(caught);
    }
  }, [desktopLayoutReady, showError]);

  useEffect(() => hasWorkbenchBridge() ? window.workbench.onChanged(() => { void synchronizeAppState(); void refreshNotificationSnapshot(); }) : undefined, [refreshNotificationSnapshot, synchronizeAppState]);

  useEffect(() => {
    if (!hasWorkbenchBridge()) return;
    let active = true;
    const unsubscribe = window.workbench.shell.onMaximizedChanged(setShellMaximized);
    void window.workbench.shell.isMaximized()
      .then((value) => { if (active) setShellMaximized(value); })
      .catch((caught) => { if (active) showError(caught); });
    return () => { active = false; unsubscribe(); };
  }, [showError]);

  useWindowMaximizeAnimation(desktopShellRef, shellMaximized, 'native');

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

  const showVirtualAppWindow = (id: string) => {
    const appMeta = APP_META[id as AppId];
    setWindows((current) => createAppWindow(current, id, appMeta?.title ?? id));
    window.setTimeout(() => focusWindow(id), 0);
  };

  const openApp = async (id: string): Promise<void> => {
    const appMeta = APP_META[id as AppId];
    const installedApp = registeredApps.find((app) => app.id === id);
    if (id !== 'app-center' && hasWorkbenchBridge() && (!installedApp?.activeVersion || !installedApp.enabled)) {
      setWindows((current) => createAppWindow(current, 'app-center', APP_META['app-center'].title));
      showNotice(installedApp?.activeVersion ? `请先在应用中心启用${appMeta?.title ?? id}。` : `请先在应用中心安装${appMeta?.title ?? id}。`);
      return;
    }
    if (id === 'app-center') {
      showVirtualAppWindow(id);
      return;
    }
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法启动应用。');
      await launchDesktopApp(id, window.workbench.apps, () => showVirtualAppWindow(id));
      if (id === 'analysis-center') void Promise.all([refreshNotificationSnapshot(true), loadAnalysisTasks()]).catch(showError);
    } catch (caught) {
      showError(caught);
    }
  };

  const closeWindow = (id: string) => setWindows((current) => current.filter((item) => item.id !== id));
  const reloadApp = async (id: string) => {
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法重新加载开发应用。');
      await window.workbench.apps.reload(id);
      setAppReloadTokens((current) => ({ ...current, [id]: (current[id] ?? 0) + 1 }));
      showNotice('开发应用已重新加载。');
    } catch (caught) {
      showError(caught);
    }
  };
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
    const installedAppIds = registeredApps.filter((item) => item.activeVersion && item.enabled).map((item) => item.id);
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
    iconDragRef.current = { id, moved: false, initialLayout: iconLayoutRef.current };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const markIconDrag = () => {
    const drag = iconDragRef.current;
    if (!drag) return;
    drag.moved = true;
  };

  const handleNotificationClick = (notification: WorkbenchNotification) => {
    setNotifications((current) => current.map((item) => item.id === notification.id ? { ...item, read: true } : item));
    setNotificationCenterOpen(false);
    const target = notification.target;
    if (target?.type === 'analysis-package') void openApp('analysis-center');
    if (target?.type === 'app') void openApp('app-center');
  };

  const unreadNotificationCount = notifications.filter((item) => !item.read).length;
  const finishIconDrag = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const drag = iconDragRef.current;
    iconDragRef.current = null;
    if (!drag?.moved) return;
    suppressOpenRef.current = true;
    const bounds = desktopIconsRef.current?.getBoundingClientRect();
    const pointer = bounds ? { x: event.clientX - bounds.left, y: event.clientY - bounds.top } : undefined;
    const reorderedLayout = pointer && resolveDesktopIconDropLayout(
      Object.entries(drag.initialLayout).map(([appId, point]) => ({ appId, ...point })),
      drag.id,
      pointer
    );
    if (!reorderedLayout) {
      window.setTimeout(() => { suppressOpenRef.current = false; }, 0);
      return;
    }
    const nextLayout = reorderedLayout.reduce<typeof iconLayout>((next, item) => ({ ...next, [item.appId]: { x: item.x, y: item.y } }), {});
    iconLayoutRef.current = nextLayout;
    setIconLayout(nextLayout);
    void saveIconLayout(nextLayout);
    window.setTimeout(() => { suppressOpenRef.current = false; }, 0);
  };

  const cancelIconDrag = () => {
    iconDragRef.current = null;
  };

  const importDroppedFiles = async (files: FileList) => {
    if (!files.length) return;
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法导入诊断包。');
      const app = registeredApps.find((item) => item.id === 'analysis-center' && item.activeVersion);
      if (!app) throw new Error('请先在应用中心安装分析中心。');
      if (!app.enabled) throw new Error('请先在应用中心启用分析中心。');
      let embeddedPresentation = false;
      await launchDesktopApp('analysis-center', window.workbench.apps, () => { embeddedPresentation = true; });
      await Promise.all([refreshNotificationSnapshot(true), loadAnalysisTasks()]);
      const result = await importAnalysisCenterFiles([...files], window.workbench.apps, false);
      if (result.failures.length) showError(result.failures.join('\n'));
      if (result.importedCount) {
        if (embeddedPresentation) showVirtualAppWindow('analysis-center');
        showNotice(`已导入 ${result.importedCount} 个诊断包。`);
      }
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
  const appLibraryIds = (Object.keys(APP_META) as AppId[]).filter((id) => id === 'app-center' || registeredApps.some((item) => item.id === id && item.activeVersion && item.enabled));

  return (
    <main ref={desktopShellRef} className="desktop-shell" style={{ '--workbench-wallpaper': `url("${WORKBENCH_WALLPAPER_URL}")` } as CSSProperties} onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); void importDroppedFiles(event.dataTransfer.files); }}>
      <div className="ambient-shape ambient-shape-one" aria-hidden="true" />
      <div className="ambient-shape ambient-shape-two" aria-hidden="true" />
      <header className="topbar">
        <div className="shell-left-tools">
          <button className="shell-launcher-button" type="button" aria-label="返回桌面" onClick={() => { setWindows((current) => current.map((item) => ({ ...item, minimized: true }))); setDrawerOpen(false); }}><Monitor size={17} /></button>
          <button className="shell-launcher-button" type="button" aria-label="打开应用库" aria-expanded={appLibraryOpen} onClick={() => setAppLibraryOpen((value) => !value)}><LayoutGrid size={17} /></button>
          {windows.length > 0 && <><span className="topbar-divider shell-left-divider" aria-hidden="true" /><div className="open-app-switcher" aria-label="已打开应用">{windows.map((item) => <button key={item.id} type="button" className={`open-app-icon ${item.minimized ? 'open-app-icon-minimized' : ''}`} aria-label={`切换到${item.title}`} title={item.title} onClick={() => focusWindow(item.id)}><img className="shell-brand-icon" src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></button>)}</div></>}
          {appLibraryOpen && <div className="app-library" role="menu" aria-label="应用库">{appLibraryIds.map((id) => { const app = APP_META[id]; return <button key={id} type="button" role="menuitem" onClick={() => { void openApp(id); setAppLibraryOpen(false); }}><img className="app-library-icon" src={APP_ICON_URLS[id]} alt="" aria-hidden="true" /><span>{app.title}</span></button>; })}</div>}
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
            onMinimize={() => void window.workbench.shell.minimize()}
            onMaximize={() => void window.workbench.shell.toggleMaximize()}
            onClose={() => void window.workbench.shell.close()}
          />
        </div>
      </header>

      <section ref={desktopIconsRef} className="desktop-icons" aria-label="应用入口">
        {(Object.keys(iconLayout) as DesktopAppId[]).map((id) => {
          const registered = registeredApps.find((item) => item.id === id);
          const meta = APP_META[id as AppId];
          const title = meta?.title ?? registered?.name ?? id;
          const description = meta?.description ?? registered?.description ?? '工作台应用';
          const point = iconLayout[id];
          // 桌面图标位置由 PointerEvent 控制，禁止图片触发 Chromium 原生拖动，避免误进入文件导入 drop。
          return <button key={id} className="desktop-icon" style={{ left: point.x, top: point.y }} type="button" onPointerDown={(event) => beginIconDrag(event, id)} onPointerMove={markIconDrag} onPointerUp={finishIconDrag} onPointerCancel={cancelIconDrag} onDragStart={(event) => event.preventDefault()} onDoubleClick={() => void openApp(id)} onClick={() => { if (!suppressOpenRef.current) void openApp(id); }} aria-label={`打开${title}`}>
            <span className={`desktop-icon-image desktop-icon-${id}`}><img draggable={false} className="desktop-brand-icon" src={resolveAppIconUrl(id)} alt="" aria-hidden="true" /></span>
            <span className="desktop-icon-label">{title}</span>
            <span className="desktop-icon-caption">{description}</span>
          </button>;
        })}
      </section>

      <div className="desktop-hint"><Menu size={14} /> 拖动图标调整顺序，单击打开应用</div>

      <section className="virtual-window-layer" aria-label="应用窗口">
        {getVisibleWindows(windows).map((item) => <VirtualWindow key={item.id} item={item} onClose={closeWindow} onFocus={focusWindow} onMinimize={toggleMinimize} onMaximize={toggleMaximize} onMove={moveVirtualWindow} onResize={(id, width, height) => setWindows((current) => resizeWindow(current, id, width, height))} onReload={registeredApps.find((app) => app.id === item.id)?.developmentOverride ? () => void reloadApp(item.id) : undefined}>
          {item.id === 'app-center' && <AppCenter onOpenApp={openApp} showError={showError} showNotice={showNotice} />}
          {item.id !== 'app-center' && <EmbeddedApp key={`${item.id}:${appReloadTokens[item.id] ?? 0}`} appId={item.id} name={item.title} showError={showError} />}
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
  onReload?: () => void;
  children: ReactNode;
}

/** 应用内虚拟窗口，只更新 React 状态，不创建额外 Electron BrowserWindow。 */
function VirtualWindow({ item, onClose, onFocus, onMinimize, onMaximize, onMove, onResize, onReload, children }: VirtualWindowProps) {
  const windowRef = useRef<HTMLElement>(null);
  const dragState = useRef<{ offsetX: number; offsetY: number } | null>(null);
  const resizeState = useRef<{ startX: number; startY: number; width: number; height: number } | null>(null);
  const style: CSSProperties = item.maximized ? { zIndex: item.zIndex } : { left: item.x, top: item.y, width: item.width, height: item.height, zIndex: item.zIndex };
  useWindowMaximizeAnimation(windowRef, item.maximized, 'virtual', `${item.x}:${item.y}:${item.width}:${item.height}`);

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

  return <article ref={windowRef} className={`app-window ${item.maximized ? 'app-window-maximized' : ''}`} style={style} onMouseDown={() => onFocus(item.id)}>
    <div className="window-titlebar" onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={stopDrag} onPointerCancel={stopDrag}>
      <div className="window-title"><span className="window-title-icon"><img className="window-brand-icon" src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></span><strong>{item.title}</strong></div>
      {onReload && <div className="window-titlebar-actions">
        {onReload && <button className="development-titlebar-reload" type="button" aria-label="重新加载开发应用" title="重新加载开发应用" onClick={onReload}><RefreshCw size={15} strokeWidth={1.5} /></button>}
      </div>}
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

interface AppCenterProps {
  onOpenApp: (id: string) => Promise<void>;
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

const APP_RUNTIME_STATE_LABELS: Record<AppCenterItem['runtimeState'], string> = {
  stopped: '已停止',
  starting: '启动中',
  running: '运行中',
  stopping: '停止中',
  failed: '运行失败'
};

/**
 * 应用中心只依赖 App Host API 获取目录和安装状态，不把具体应用的业务逻辑写进工作台壳层。
 * 首版保留官方分析中心入口，后续应用可以仅通过目录和安装包加入此页面。
 */
function AppCenter({ onOpenApp, showError, showNotice }: AppCenterProps) {
  const [apps, setApps] = useState<AppCenterItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [busyAppId, setBusyAppId] = useState<string | null>(null);
  const [uninstallTarget, setUninstallTarget] = useState<AppCenterItem | null>(null);
  const [deleteData, setDeleteData] = useState(false);

  const beginBusy = (appId: string) => setBusyAppId((current) => beginAppOperation({ activeAppId: current }, appId).activeAppId);
  const finishBusy = (appId: string) => setBusyAppId((current) => completeAppOperation({ activeAppId: current }, appId).activeAppId);

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

  const install = async (item: AppCenterItem) => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法安装应用。'); return; }
    if (busyAppId) return;
    beginBusy(item.id);
    try {
      await window.workbench.apps.install(item.id, item.availableVersion);
      setApps(await window.workbench.apps.list());
      showNotice(`${item.name} 已安装完成。`);
    } catch (caught) {
      showError(caught);
    } finally {
      finishBusy(item.id);
    }
  };

  const setEnabled = async (item: AppCenterItem, enabled: boolean) => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法修改应用状态。'); return; }
    if (busyAppId) return;
    beginBusy(item.id);
    try {
      const updated = await window.workbench.apps.setEnabled(item.id, enabled);
      setApps((current) => current.map((candidate) => candidate.id === item.id ? updated : candidate));
      showNotice(enabled ? `${item.name} 已启用。` : `${item.name} 已停用。`);
    } catch (caught) {
      showError(caught);
    } finally {
      finishBusy(item.id);
    }
  };

  const launch = async (item: AppInstallRecord) => {
    if (!hasWorkbenchBridge()) { showError('工作台接口尚未就绪，无法启动应用。'); return; }
    if (busyAppId) return;
    beginBusy(item.id);
    try {
      await onOpenApp(item.id);
    } catch (caught) {
      showError(caught);
    } finally {
      finishBusy(item.id);
    }
  };

  const requestUninstall = (item: AppCenterItem) => {
    setDeleteData(false);
    setUninstallTarget(item);
  };

  const confirmUninstall = async () => {
    if (!uninstallTarget || !hasWorkbenchBridge() || busyAppId) return;
    const target = uninstallTarget;
    beginBusy(target.id);
    try {
      await window.workbench.apps.uninstall(target.id, deleteData);
      setUninstallTarget(null);
      setDeleteData(false);
      setApps(await window.workbench.apps.list());
      showNotice(`${target.name} 已卸载。`);
    } catch (caught) {
      showError(caught);
    } finally {
      finishBusy(target.id);
    }
  };

  const renderAction = (item: AppCenterItem) => {
    const busy = isAppOperationBusy({ activeAppId: busyAppId }) || item.state === 'installing';
    if (item.state === 'not-installed' || item.state === 'update-available') {
      return <button type="button" className="primary-button" disabled={busy} onClick={() => void install(item)}>{busy ? <LoaderCircle className="spin" size={15} /> : <CloudDownload size={15} />}{item.state === 'not-installed' ? '安装' : '更新'}</button>;
    }
    if (item.state === 'installed') {
      return <button type="button" className="primary-button" disabled={!item.enabled || busy} onClick={() => void launch(item)}>{busy ? <LoaderCircle className="spin" size={15} /> : <Play size={15} />}打开</button>;
    }
    return null;
  };

  return <div className="app-center-view">
    <div className="app-center-toolbar"><button type="button" className="secondary-button" disabled={refreshing} onClick={() => void refreshCatalog()}>{refreshing ? <LoaderCircle className="spin" size={15} /> : <RefreshCw size={15} />}刷新目录</button></div>
     {loading ? <div className="app-center-empty"><LoaderCircle className="spin" size={24} /><span>正在读取应用目录…</span></div> : apps.length === 0 ? <div className="app-center-empty"><PackageOpen size={28} /><strong>暂无可用应用</strong><span>请刷新目录，或检查应用目录配置。</span></div> : <div className="app-card-grid">{apps.map((item) => {
       const busy = isAppOperationBusy({ activeAppId: busyAppId }) || item.state === 'installing';
       const installed = Boolean(item.activeVersion);
       return <article className="app-card" key={item.id} aria-busy={busy}>
         <div className="app-card-icon"><img src={resolveAppIconUrl(item.id)} alt="" aria-hidden="true" /></div>
         <div className="app-card-body">
           <div className="app-card-title"><h2>{item.name}</h2><span className={`app-state app-state-${item.state}`}>{APP_STATE_LABELS[item.state]}</span></div>
           <p>{item.description}</p>
           <small>{item.activeVersion ? `当前版本 ${item.activeVersion}` : item.availableVersion ? `可安装版本 ${item.availableVersion}` : '等待目录信息'}</small>
           {installed && <div className="app-card-runtime"><span>运行状态</span><strong data-runtime-state={item.runtimeState}>{APP_RUNTIME_STATE_LABELS[item.runtimeState]}</strong></div>}
           {item.errorMessage && <div className="app-card-error"><CircleAlert size={14} />{item.errorMessage}</div>}
         </div>
         <div className="app-card-actions">
           {installed && <label className="app-enabled-toggle">
             <input type="checkbox" checked={item.enabled} disabled={busy} aria-label={`启用${item.name}`} aria-busy={busy} onChange={(event) => void setEnabled(item, event.target.checked)} />
             <span className="app-enabled-toggle-track" aria-hidden="true" />
             <span>{item.enabled ? '已启用' : '已停用'}</span>
           </label>}
           {installed && (item.builtIn ? <span className="app-built-in">内置</span> : <button type="button" className="app-uninstall-button" title="卸载应用" aria-label={`卸载${item.name}`} disabled={busy} onClick={() => requestUninstall(item)}><Trash2 size={16} /></button>)}
           {renderAction(item)}
         </div>
       </article>;
     })}</div>}
     {uninstallTarget && <UninstallDialog item={uninstallTarget} deleteData={deleteData} busy={isAppOperationBusy({ activeAppId: busyAppId })} onDeleteDataChange={setDeleteData} onCancel={() => { if (!busyAppId) setUninstallTarget(null); }} onConfirm={() => void confirmUninstall()} />}
   </div>;
}

/** 卸载确认明确区分保留数据与永久删除，默认只移除应用包和入口。 */
function UninstallDialog({ item, deleteData, busy, onDeleteDataChange, onCancel, onConfirm }: {
  item: AppCenterItem;
  deleteData: boolean;
  busy: boolean;
  onDeleteDataChange: (value: boolean) => void;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const cancelButtonRef = useRef<HTMLButtonElement>(null);
  const didFocusCancelRef = useRef(false);

  useEffect(() => {
    if (didFocusCancelRef.current) return;
    cancelButtonRef.current?.focus();
    didFocusCancelRef.current = true;
  }, []);

  useEffect(() => {
    // 忙碌时必须保持卸载确认框，Escape 监听只在组件存在期间生效并在卸载时清理。
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !busy) onCancel();
    };
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, [busy, onCancel]);

  return <div className="modal-backdrop" role="presentation"><section className="confirm-dialog app-uninstall-dialog" role="dialog" aria-modal="true" aria-labelledby="app-uninstall-dialog-title">
    <div className="dialog-icon"><Trash2 size={22} /></div>
    <div className="dialog-heading"><span className="eyebrow danger-eyebrow">APP UNINSTALL</span><h2 id="app-uninstall-dialog-title">卸载 {item.name}？</h2><p>默认保留应用数据。确认后将移除应用包和桌面入口，正在运行的应用也会被关闭。</p></div>
    <label className="confirm-check"><input type="checkbox" checked={deleteData} disabled={busy} aria-label="同时删除应用数据" onChange={(event) => onDeleteDataChange(event.target.checked)} /><span className="custom-checkbox" aria-hidden="true">{deleteData ? '✓' : ''}</span><span>永久删除配置、历史记录和报告，此操作不可恢复。</span></label>
    <div className="dialog-actions"><button ref={cancelButtonRef} type="button" className="secondary-button" disabled={busy} onClick={onCancel}>取消</button><button type="button" className="danger-button" disabled={busy} onClick={onConfirm}>{busy ? <LoaderCircle className="spin" size={15} /> : <Trash2 size={15} />}卸载</button></div>
  </section></div>;
}

interface EmbeddedAppProps { appId: string; name: string; showError: (error: unknown) => void; }

/** 未内置到壳层的应用通过 workbench-app 协议加载自己的 renderer 资源。 */
function EmbeddedApp({ appId, name, showError }: EmbeddedAppProps) {
  const [entryUrl, setEntryUrl] = useState('');
  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法加载应用。');
        const url = await window.workbench.apps.getEntryUrl(appId);
        if (active) setEntryUrl(url);
      } catch (caught) { if (active) showError(caught); }
    })();
    return () => { active = false; };
  }, [appId, showError]);
  return entryUrl ? <HostedAppSurface appId={appId} name={name} entryUrl={entryUrl} onError={showError} /> : <div className="app-center-empty"><LoaderCircle className="spin" size={22} /><span>正在加载应用…</span></div>;
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
