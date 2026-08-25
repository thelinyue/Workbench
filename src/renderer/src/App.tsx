import {
  Activity,
  Archive,
  ArrowDownToLine,
  BarChart3,
  Check,
  CheckCircle2,
  ChevronDown,
  CircleAlert,
  ClipboardList,
  CloudDownload,
  Copy,
  Cpu,
  Database,
  ExternalLink,
  FileArchive,
  FolderOpen,
  HardDrive,
  LayoutGrid,
  ListChecks,
  LoaderCircle,
  Maximize2,
  Menu,
  Minus,
  Minimize2,
  Monitor,
  MoreHorizontal,
  PackageOpen,
  Play,
  RefreshCw,
  Search,
  Settings as SettingsIcon,
  ShieldCheck,
  Trash2,
  Upload,
  X,
  type LucideIcon
} from 'lucide-react';
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type PointerEvent as ReactPointerEvent, type ReactNode } from 'react';
import { createAppWindow, minimizeWindow, moveWindow, resizeWindow, type AppWindow } from '../window-manager';
import {
  formatBytes,
  formatDetectedAt,
  getBulkDeletablePackages,
  sortLatestPackages,
  statusLabels,
  statusTone,
  toChineseError,
  type RendererDiagnosticPackage
} from './ui-model';

type AppId = 'analysis-center' | 'settings';

interface TaskRecord {
  id: string;
  packageId: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  createdAt: string;
  progress: number;
  message: string;
  errorMessage?: string;
}

interface DeletePreview {
  packageCount: number;
  taskCount: number;
  sourcePaths: string[];
  extractPaths: string[];
  reportPaths: string[];
  estimatedBytes: number;
  confirmationToken: string;
  caseCount: number;
  analysisRecordCount: number;
  reportRecordCount: number;
}

interface ContextMenuState {
  packageItem: RendererDiagnosticPackage;
  x: number;
  y: number;
}

interface DeleteDialogState {
  packageIds: string[];
  preview: DeletePreview;
}

const APP_META: Record<AppId, { title: string; description: string; icon: LucideIcon }> = {
  'analysis-center': { title: '分析中心', description: '诊断包与日志报告', icon: BarChart3 },
  settings: { title: '设置', description: '工作台偏好设置', icon: SettingsIcon }
};

const DEFAULT_ICON_LAYOUT: Record<AppId, { x: number; y: number }> = {
  'analysis-center': { x: 44, y: 96 },
  settings: { x: 44, y: 238 }
};

function hasWorkbenchBridge(): boolean {
  return typeof window !== 'undefined' && Boolean(window.workbench);
}

function fallbackDeletionPreview(packages: RendererDiagnosticPackage[]): DeletePreview {
  return {
    packageCount: packages.length,
    taskCount: packages.reduce((total, item) => total + item.taskIds.length, 0),
    sourcePaths: packages.map((item) => item.sourcePath),
    extractPaths: packages.map((item) => item.extractPath),
    reportPaths: packages.flatMap((item) => item.reportPath ? [item.reportPath] : []),
    estimatedBytes: 0,
    confirmationToken: '',
    caseCount: packages.length,
    analysisRecordCount: 0,
    reportRecordCount: 0
  };
}

/**
 * 工作台根壳层：管理应用图标、虚拟窗口层、任务抽屉和跨窗口错误提示。
 * 所有本地能力均通过 window.workbench 进入，渲染进程本身不访问 Node 或文件系统。
 */
export function App() {
  const [windows, setWindows] = useState<AppWindow[]>([]);
  const [tasks, setTasks] = useState<TaskRecord[]>([]);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [appLibraryOpen, setAppLibraryOpen] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [iconLayout, setIconLayout] = useState(DEFAULT_ICON_LAYOUT);
  const iconLayoutRef = useRef(iconLayout);
  const iconDragRef = useRef<{ id: AppId; offsetX: number; offsetY: number; moved: boolean } | null>(null);
  const suppressOpenRef = useRef(false);

  useEffect(() => { iconLayoutRef.current = iconLayout; }, [iconLayout]);

  const showError = useCallback((message: unknown) => {
    setError(toChineseError(message));
    setNotice('');
  }, []);

  const showNotice = useCallback((message: string) => {
    setNotice(message);
    setError('');
  }, []);

  const refreshTasks = useCallback(async () => {
    try {
      if (!hasWorkbenchBridge()) return;
      setTasks(await window.workbench.tasks.list());
    } catch (caught) {
      showError(caught);
    }
  }, [showError]);

  useEffect(() => {
    let active = true;
    void (async () => {
      try {
        if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，请启动 Electron 主进程后重试。');
        const savedLayout = await window.workbench.desktop.loadLayout();
        if (active && savedLayout.length) {
          setIconLayout((current) => savedLayout.reduce((next, item) => ({ ...next, [item.appId]: { x: item.x, y: item.y } }), current));
        }
        await refreshTasks();
      } catch (caught) {
        if (active) showError(caught);
      }
    })();
    return () => { active = false; };
  }, [refreshTasks, showError]);

  useEffect(() => {
    if (!hasWorkbenchBridge()) return;
    const timer = window.setInterval(() => { void refreshTasks(); }, 2500);
    return () => window.clearInterval(timer);
  }, [refreshTasks]);

  useEffect(() => hasWorkbenchBridge() ? window.workbench.onChanged(() => { void refreshTasks(); }) : undefined, [refreshTasks]);

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setDrawerOpen(false);
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

  const openApp = (id: AppId) => {
    setWindows((current) => createAppWindow(current, id, APP_META[id].title));
    window.setTimeout(() => focusWindow(id), 0);
  };

  const closeWindow = (id: string) => setWindows((current) => current.filter((item) => item.id !== id));
  const toggleMinimize = (id: string) => setWindows((current) => minimizeWindow(current, id));
  const toggleMaximize = (id: string) => setWindows((current) => current.map((item) => item.id === id ? { ...item, maximized: !item.maximized, minimized: false } : item));
  const moveVirtualWindow = (id: string, x: number, y: number) => setWindows((current) => moveWindow(current, id, Math.max(12, x), Math.max(52, y)));

  const saveIconLayout = async (nextLayout: typeof iconLayout) => {
    setIconLayout(nextLayout);
    try {
      if (hasWorkbenchBridge()) await window.workbench.desktop.saveLayout(Object.entries(nextLayout).map(([appId, point]) => ({ appId: appId as AppId, ...point })));
    } catch (caught) {
      showError(caught);
    }
  };

  const beginIconDrag = (event: ReactPointerEvent<HTMLButtonElement>, id: AppId) => {
    const point = iconLayoutRef.current[id];
    iconDragRef.current = { id, offsetX: event.clientX - point.x, offsetY: event.clientY - point.y, moved: false };
    event.currentTarget.setPointerCapture(event.pointerId);
  };
  const moveIcon = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const drag = iconDragRef.current;
    if (!drag) return;
    drag.moved = true;
    const next = { ...iconLayoutRef.current, [drag.id]: { x: Math.max(12, event.clientX - drag.offsetX), y: Math.max(52, event.clientY - drag.offsetY) } };
    iconLayoutRef.current = next;
    setIconLayout(next);
  };
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
      const imported = await window.workbench.analysis.importDroppedFiles(Array.from(files));
      if (imported.length) {
        showNotice(`已导入 ${imported.length} 个诊断包。`);
        openApp('analysis-center');
      }
    } catch (caught) { showError(caught); }
  };

  const runningCount = tasks.filter((task) => task.status === 'running' || task.status === 'queued').length;

  return (
    <main className="desktop-shell" onDragOver={(event) => event.preventDefault()} onDrop={(event) => { event.preventDefault(); void importDroppedFiles(event.dataTransfer.files); }}>
      <div className="ambient-shape ambient-shape-one" aria-hidden="true" />
      <div className="ambient-shape ambient-shape-two" aria-hidden="true" />
      <header className="topbar">
        <div className="shell-left-tools">
          <button className="shell-launcher-button" type="button" aria-label="返回桌面" onClick={() => { setWindows((current) => current.map((item) => ({ ...item, minimized: true }))); setDrawerOpen(false); }}><Monitor size={17} /></button>
          <button className="shell-launcher-button" type="button" aria-label="打开应用库" aria-expanded={appLibraryOpen} onClick={() => setAppLibraryOpen((value) => !value)}><LayoutGrid size={17} /></button>
          {windows.length > 0 && <><span className="topbar-divider shell-left-divider" aria-hidden="true" /><div className="open-app-switcher" aria-label="已打开应用">{windows.map((item) => { const Icon = APP_META[item.id as AppId].icon; return <button key={item.id} type="button" className={`open-app-icon ${item.minimized ? 'open-app-icon-minimized' : ''}`} aria-label={`切换到${item.title}`} title={item.title} onClick={() => focusWindow(item.id)}><Icon size={16} /></button>; })}</div></>}
          {appLibraryOpen && <div className="app-library" role="menu" aria-label="应用库">{(Object.keys(APP_META) as AppId[]).map((id) => { const app = APP_META[id]; const Icon = app.icon; return <button key={id} type="button" role="menuitem" onClick={() => { openApp(id); setAppLibraryOpen(false); }}><Icon size={18} /><span>{app.title}</span></button>; })}</div>}
        </div>
        <div className="topbar-actions">
          <div className="health-indicator"><span className="health-dot" />系统在线</div>
          <button className="topbar-icon-button" type="button" aria-label={`打开任务中心${runningCount ? `，${runningCount} 项进行中` : ''}`} aria-expanded={drawerOpen} onClick={() => setDrawerOpen((value) => !value)}>
            <Activity size={18} />{runningCount > 0 && <span className="notification-dot">{runningCount}</span>}
          </button>
          <button className="topbar-icon-button" type="button" aria-label="打开设置" onClick={() => openApp('settings')}><SettingsIcon size={18} /></button>
          <div className="shell-window-controls" aria-label="窗口控制">
            <button className="shell-window-control" type="button" aria-label="最小化工作台" onClick={() => void window.workbench.shell.minimize()}><Minus size={18} strokeWidth={1.8} /></button>
            <button className="shell-window-control" type="button" aria-label="最大化或还原工作台" onClick={() => void window.workbench.shell.toggleMaximize()}><Maximize2 size={16} strokeWidth={1.7} /></button>
            <button className="shell-window-control shell-window-control-close" type="button" aria-label="关闭工作台" onClick={() => void window.workbench.shell.close()}><X size={18} strokeWidth={1.7} /></button>
          </div>
        </div>
      </header>

      <section className="desktop-icons" aria-label="应用入口">
        {(Object.keys(APP_META) as AppId[]).map((id) => {
          const meta = APP_META[id];
          const Icon = meta.icon;
          const point = iconLayout[id];
          return <button key={id} className="desktop-icon" style={{ left: point.x, top: point.y }} type="button" onPointerDown={(event) => beginIconDrag(event, id)} onPointerMove={moveIcon} onPointerUp={finishIconDrag} onPointerCancel={finishIconDrag} onDoubleClick={() => openApp(id)} onClick={() => { if (!suppressOpenRef.current) openApp(id); }} aria-label={`打开${meta.title}`}>
            <span className={`desktop-icon-image desktop-icon-${id}`}><Icon size={30} strokeWidth={1.7} /></span>
            <span className="desktop-icon-label">{meta.title}</span>
            <span className="desktop-icon-caption">{meta.description}</span>
          </button>;
        })}
      </section>

      <div className="desktop-hint"><Menu size={14} /> 拖动图标整理桌面，单击打开应用</div>

      <section className="virtual-window-layer" aria-label="应用窗口">
        {windows.map((item) => <VirtualWindow key={item.id} item={item} onClose={closeWindow} onFocus={focusWindow} onMinimize={toggleMinimize} onMaximize={toggleMaximize} onMove={moveVirtualWindow} onResize={(id, width, height) => setWindows((current) => resizeWindow(current, id, width, height))}>
          {item.id === 'analysis-center' && <AnalysisCenter showError={showError} showNotice={showNotice} />}
          {item.id === 'settings' && <SettingsWindow showError={showError} showNotice={showNotice} />}
        </VirtualWindow>)}
      </section>

      <TaskDrawer open={drawerOpen} tasks={tasks} onClose={() => setDrawerOpen(false)} onCancel={async (taskId) => {
        try { await window.workbench.tasks.cancel(taskId); showNotice('任务已取消。'); await refreshTasks(); } catch (caught) { showError(caught); }
      }} />

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
  children: ReactNode;
}

/** 应用内虚拟窗口，只更新 React 状态，不创建额外 Electron BrowserWindow。 */
function VirtualWindow({ item, onClose, onFocus, onMinimize, onMaximize, onMove, onResize, children }: VirtualWindowProps) {
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

  return <article className={`app-window ${item.maximized ? 'app-window-maximized' : ''} ${item.minimized ? 'app-window-minimized' : ''}`} style={style} onMouseDown={() => onFocus(item.id)}>
    <div className="window-titlebar" onPointerDown={onPointerDown} onPointerMove={onPointerMove} onPointerUp={stopDrag} onPointerCancel={stopDrag}>
      <div className="window-title"><span className="window-title-icon"><APP_META_ICON id={item.id as AppId} /></span><strong>{item.title}</strong></div>
      <div className="window-controls">
        <button type="button" aria-label={`最小化${item.title}`} onClick={() => onMinimize(item.id)}><Minimize2 size={14} /></button>
        <button type="button" aria-label={`${item.maximized ? '还原' : '最大化'}${item.title}`} onClick={() => onMaximize(item.id)}><Maximize2 size={14} /></button>
        <button className="window-close" type="button" aria-label={`关闭${item.title}`} onClick={() => onClose(item.id)}><X size={15} /></button>
      </div>
    </div>
    {!item.minimized && <div className="window-content">{children}</div>}
    {!item.maximized && !item.minimized && <div className="app-window-resizer" aria-label="调整窗口大小" onPointerDown={onResizeStart} onPointerMove={onResizeMove} onPointerUp={() => { resizeState.current = null; }} onPointerCancel={() => { resizeState.current = null; }} />}
  </article>;
}

function APP_META_ICON({ id }: { id: AppId }) {
  const Icon = APP_META[id].icon;
  return <Icon size={16} aria-hidden="true" />;
}

interface AnalysisCenterProps { showError: (error: unknown) => void; showNotice: (message: string) => void; }

/** 分析中心主视图：把导入、扫描、分析、删除和报告入口集中到“最新诊断包”网格。 */
function AnalysisCenter({ showError, showNotice }: AnalysisCenterProps) {
  const [packages, setPackages] = useState<RendererDiagnosticPackage[]>([]);
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null);
  const [deleteDialog, setDeleteDialog] = useState<DeleteDialogState | null>(null);
  const [batchAnalysisOpen, setBatchAnalysisOpen] = useState(false);
  const [confirmPermanent, setConfirmPermanent] = useState(false);
  const [busyAction, setBusyAction] = useState('');

  const refreshPackages = useCallback(async () => {
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法读取诊断包。');
      setPackages(await window.workbench.analysis.list());
    } catch (caught) { showError(caught); }
  }, [showError]);

  useEffect(() => { void refreshPackages(); }, [refreshPackages]);
  useEffect(() => hasWorkbenchBridge() ? window.workbench.onChanged(() => { void refreshPackages(); }) : undefined, [refreshPackages]);
  useEffect(() => {
    const closeMenu = () => setContextMenu(null);
    window.addEventListener('click', closeMenu);
    return () => window.removeEventListener('click', closeMenu);
  }, []);
  useEffect(() => {
    const closeOverlays = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      setContextMenu(null);
      setDeleteDialog(null);
      setBatchAnalysisOpen(false);
    };
    window.addEventListener('keydown', closeOverlays);
    return () => window.removeEventListener('keydown', closeOverlays);
  }, []);

  const sortedPackages = useMemo(() => sortLatestPackages(packages), [packages]);
  const selectedPackages = packages.filter((item) => selectedIds.includes(item.id));
  const deletableSelected = selectedPackages.filter((item) => item.status !== 'running' && item.status !== 'queued');
  const completedOrFailed = getBulkDeletablePackages(packages);
  const runningPackages = packages.filter((item) => item.status === 'running');

  const runAction = async (key: string, action: () => Promise<void>, success: string) => {
    setBusyAction(key);
    try { await action(); showNotice(success); await refreshPackages(); }
    catch (caught) { showError(caught); }
    finally { setBusyAction(''); }
  };

  const importPackage = () => runAction('import', async () => {
    if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法导入诊断包。');
    await window.workbench.analysis.importPackage();
  }, '诊断包已导入。');

  const scanDirectory = () => runAction('scan', async () => {
    if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法扫描监控目录。');
    await window.workbench.analysis.scan();
  }, '监控目录扫描完成。');

  const pendingPackages = packages.filter((item) => item.status === 'pending');

  const requestAnalyzeAll = () => {
    if (pendingPackages.length === 0) { showNotice('当前没有待分析的诊断包。'); return; }
    setBatchAnalysisOpen(true);
  };

  const analyzeAll = () => runAction('all', async () => {
    if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法创建分析任务。');
    await window.workbench.analysis.startAllPending();
    setBatchAnalysisOpen(false);
  }, '已创建批量分析任务。');

  const analyzeOne = (item: RendererDiagnosticPackage, scope: 'comprehensive' | 'storage' = 'comprehensive') => runAction(`analyze-${item.id}`, async () => {
    if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法创建分析任务。');
    await window.workbench.analysis.start(item.id, scope);
  }, `已开始${scope === 'storage' ? '存储健康分析' : '综合分析'} ${item.displayName}。`);

  const openReport = async (item: RendererDiagnosticPackage) => {
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法打开报告。');
      await window.workbench.analysis.openReport(item.id);
    } catch (caught) { showError(caught); }
  };

  const locate = async (item: RendererDiagnosticPackage, kind: 'source' | 'extract') => {
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法定位文件。');
      if (kind === 'source') await window.workbench.analysis.locateSource(item.id);
      else await window.workbench.analysis.locateExtract(item.id);
    } catch (caught) { showError(caught); }
  };

  const requestDelete = async (items: RendererDiagnosticPackage[]) => {
    const safeItems = items.filter((item) => item.status !== 'running' && item.status !== 'queued');
    if (!safeItems.length) { showError('没有可删除的诊断包。运行中或排队中的诊断包不能删除。'); return; }
    try {
      const preview = hasWorkbenchBridge() ? await window.workbench.analysis.deletePreview(safeItems.map((item) => item.id)) : fallbackDeletionPreview(safeItems);
      setConfirmPermanent(false);
      setDeleteDialog({ packageIds: safeItems.map((item) => item.id), preview });
      setContextMenu(null);
    } catch (caught) { showError(caught); }
  };

  const confirmDelete = async () => {
    if (!deleteDialog || !confirmPermanent) return;
    setBusyAction('delete');
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法删除诊断包。');
      await window.workbench.analysis.deletePackages(deleteDialog.packageIds, deleteDialog.preview.confirmationToken);
      setSelectedIds((current) => current.filter((id) => !deleteDialog.packageIds.includes(id)));
      setDeleteDialog(null);
      showNotice('诊断包、解压目录、报告和分析记录已永久删除。');
      await refreshPackages();
    } catch (caught) { showError(caught); }
    finally { setBusyAction(''); }
  };

  const toggleSelected = (id: string) => setSelectedIds((current) => current.includes(id) ? current.filter((item) => item !== id) : [...current, id]);
  const selectablePackages = sortedPackages.filter((item) => item.status !== 'running' && item.status !== 'queued');
  const toggleAll = () => setSelectedIds(selectedIds.length === selectablePackages.length ? [] : selectablePackages.map((item) => item.id));
  const rightClick = (event: React.MouseEvent, item: RendererDiagnosticPackage) => { event.preventDefault(); setContextMenu({ packageItem: item, x: Math.min(event.clientX, window.innerWidth - 210), y: Math.min(event.clientY, window.innerHeight - 180) }); };

  return <div className="analysis-view" onContextMenu={(event) => event.preventDefault()}>
    <div className="analysis-heading">
      <div><span className="eyebrow">SYSTEM DIAGNOSTICS</span><h1>分析中心</h1><p>导入诊断包，快速完成系统日志分析并查看报告。</p></div>
      <div className="analysis-heading-metric"><span>最新诊断包</span><strong>{packages.length.toString().padStart(2, '0')}</strong></div>
    </div>

    <div className="analysis-actions" aria-label="分析操作">
      <ActionTile icon={Upload} label="导入诊断包" hint="选择 .tgz / .tgz.temp" onClick={importPackage} busy={busyAction === 'import'} accent="violet" />
      <ActionTile icon={RefreshCw} label="扫描监控目录" hint="手动发现新文件" onClick={scanDirectory} busy={busyAction === 'scan'} accent="blue" />
      <ActionTile icon={Play} label="分析全部待处理" hint="批量启动分析" onClick={requestAnalyzeAll} busy={busyAction === 'all'} accent="amber" />
    </div>

    <div className="analysis-toolbar">
      <div className="section-title"><FileArchive size={18} /><div><strong>最新诊断包</strong><span>按最近导入或检测时间排列</span></div></div>
      <div className="toolbar-actions">
        {completedOrFailed.length > 0 && <button type="button" className="quiet-button" onClick={() => setSelectedIds(completedOrFailed.map((item) => item.id))}><CheckCircle2 size={15} />选择已完成 / 失败</button>}
        {selectedIds.length > 0 && <button type="button" className="danger-button" onClick={() => void requestDelete(deletableSelected)}><Trash2 size={15} />删除所选（{deletableSelected.length}）</button>}
        {sortedPackages.length > 0 && <button type="button" className="icon-only-button" aria-label={selectedIds.length === selectablePackages.length ? '取消全选' : '选择可删除项目'} onClick={toggleAll}><ListChecks size={18} /></button>}
      </div>
    </div>

    {sortedPackages.length === 0 ? <EmptyPackages onImport={importPackage} onScan={scanDirectory} busyAction={busyAction} /> : <div className="package-grid">
      {sortedPackages.map((item) => <PackageCard key={item.id} item={item} selected={selectedIds.includes(item.id)} onToggle={() => toggleSelected(item.id)} onContextMenu={(event) => rightClick(event, item)} onAnalyze={() => analyzeOne(item)} onOpenReport={() => void openReport(item)} busy={busyAction === `analyze-${item.id}`} />)}
    </div>}

    <div className="analysis-footer"><span><ShieldCheck size={14} />删除诊断包会同时删除原始包、解压目录、报告与分析记录</span><span>{runningPackages.length > 0 ? `${runningPackages.length} 个诊断包正在分析，暂不可删除` : '右键诊断包查看更多操作'}</span></div>

    {contextMenu && <ContextMenu menu={contextMenu} onAnalyze={() => { setContextMenu(null); void analyzeOne(contextMenu.packageItem); }} onStorageAnalyze={() => { setContextMenu(null); void analyzeOne(contextMenu.packageItem, 'storage'); }} onLocateSource={() => void locate(contextMenu.packageItem, 'source')} onLocateExtract={() => void locate(contextMenu.packageItem, 'extract')} onDelete={() => void requestDelete([contextMenu.packageItem])} />}
    {deleteDialog && <DeleteDialog dialog={deleteDialog} confirmPermanent={confirmPermanent} busy={busyAction === 'delete'} onChange={setConfirmPermanent} onCancel={() => setDeleteDialog(null)} onConfirm={() => void confirmDelete()} />}
    {batchAnalysisOpen && <BatchAnalysisDialog packages={pendingPackages} busy={busyAction === 'all'} onCancel={() => setBatchAnalysisOpen(false)} onConfirm={() => void analyzeAll()} />}
  </div>;
}

function ActionTile({ icon: Icon, label, hint, onClick, busy, accent }: { icon: LucideIcon; label: string; hint: string; onClick: () => void; busy: boolean; accent: string }) {
  return <button className={`action-tile action-tile-${accent}`} type="button" onClick={onClick} disabled={busy} aria-label={label}>{busy ? <LoaderCircle className="spin" size={22} /> : <Icon size={22} />}<span><strong>{label}</strong><small>{busy ? '处理中…' : hint}</small></span><ChevronDown className="action-arrow" size={15} /></button>;
}

function EmptyPackages({ onImport, onScan, busyAction }: { onImport: () => void; onScan: () => void; busyAction: string }) {
  return <div className="empty-packages"><div className="empty-icon"><Archive size={28} /></div><h2>还没有诊断包</h2><p>导入一个 .tgz 或 .tgz.temp 文件，或者扫描已配置的监控目录。</p><div className="empty-actions"><button type="button" className="primary-button" onClick={onImport} disabled={busyAction === 'import'}><Upload size={16} />导入诊断包</button><button type="button" className="secondary-button" onClick={onScan} disabled={busyAction === 'scan'}><RefreshCw size={16} />扫描目录</button></div></div>;
}

function PackageCard({ item, selected, onToggle, onContextMenu, onAnalyze, onOpenReport, busy }: { item: RendererDiagnosticPackage; selected: boolean; onToggle: () => void; onContextMenu: (event: React.MouseEvent) => void; onAnalyze: () => void; onOpenReport: () => void; busy: boolean }) {
  const isBusy = item.status === 'running' || item.status === 'queued';
  const canDelete = !isBusy;
  return <article className={`package-card ${selected ? 'package-card-selected' : ''}`} onContextMenu={onContextMenu}>
    <div className="package-card-top"><label className="checkbox-wrap"><input type="checkbox" checked={selected} disabled={!canDelete} onChange={onToggle} aria-label={`选择${item.displayName}进行删除`} /><span className="custom-checkbox">{selected && <Check size={12} />}</span></label><span className={`status-badge status-${statusTone[item.status]}`}><span className="status-dot" />{statusLabels[item.status]}</span><button className="card-more" type="button" aria-label={`打开${item.displayName}的快捷菜单`} onClick={(event) => { event.stopPropagation(); onContextMenu(event); }}><MoreHorizontal size={16} /></button></div>
    <div className="package-icon"><FileArchive size={30} /></div>
    <h3 title={item.displayName}>{item.displayName}</h3><p className="package-time">检测于 {formatDetectedAt(item.detectedAt)}</p>
    <div className="package-path" title={item.sourcePath}><FolderOpen size={13} />{item.sourcePath}</div>
    <div className="package-card-actions">{item.status === 'report-ready' ? <button type="button" className="report-button" onClick={onOpenReport}><ExternalLink size={14} />打开报告</button> : <button type="button" className="card-action-button" disabled={isBusy || busy} onClick={onAnalyze}>{busy ? <LoaderCircle className="spin" size={14} /> : <Play size={14} />}{isBusy ? '分析中' : '分析'}</button>}<span className="card-hint">右键查看更多</span></div>
  </article>;
}

function ContextMenu({ menu, onAnalyze, onStorageAnalyze, onLocateSource, onLocateExtract, onDelete }: { menu: ContextMenuState; onAnalyze: () => void; onStorageAnalyze: () => void; onLocateSource: () => void; onLocateExtract: () => void; onDelete: () => void }) {
  const busy = menu.packageItem.status === 'running' || menu.packageItem.status === 'queued';
  return <div className="context-menu" role="menu" style={{ left: menu.x, top: menu.y }} onClick={(event) => event.stopPropagation()}>
    <div className="context-menu-title" title={menu.packageItem.displayName}>{menu.packageItem.displayName}</div>
    <button type="button" role="menuitem" disabled={busy} onClick={onAnalyze}><Play size={15} />分析</button>
    <button type="button" role="menuitem" disabled={busy} onClick={onStorageAnalyze}><HardDrive size={15} />仅存储健康分析</button>
    <div className="context-divider" />
    <button type="button" role="menuitem" onClick={onLocateSource}><FolderOpen size={15} />定位诊断包</button>
    <button type="button" role="menuitem" onClick={onLocateExtract}><Archive size={15} />定位解压目录</button>
    <div className="context-divider" />
    <button className="context-danger" type="button" role="menuitem" disabled={busy} onClick={onDelete}><Trash2 size={15} />删除诊断包</button>
  </div>;
}

function DeleteDialog({ dialog, confirmPermanent, busy, onChange, onCancel, onConfirm }: { dialog: DeleteDialogState; confirmPermanent: boolean; busy: boolean; onChange: (value: boolean) => void; onCancel: () => void; onConfirm: () => void }) {
  const { preview } = dialog;
  return <div className="modal-backdrop" role="presentation"><section className="confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="delete-dialog-title">
    <div className="dialog-icon"><Trash2 size={22} /></div><div className="dialog-heading"><span className="eyebrow danger-eyebrow">PERMANENT DELETE</span><h2 id="delete-dialog-title">永久删除诊断包？</h2><p>将删除选中的 {preview.packageCount} 个诊断包及其完整分析生命周期，此操作无法恢复。</p></div>
    <div className="delete-summary"><div><span>关联任务</span><strong>{preview.taskCount}</strong></div><div><span>分析记录</span><strong>{preview.analysisRecordCount}</strong></div><div><span>案例 / 报告索引</span><strong>{preview.caseCount} / {preview.reportRecordCount}</strong></div><div><span>预计释放</span><strong>{formatBytes(preview.estimatedBytes)}</strong></div></div>
    <div className="delete-paths"><strong>将永久删除的绝对路径</strong>{[...preview.sourcePaths, ...preview.extractPaths, ...preview.reportPaths].map((path) => <code key={path}>{path}</code>)}</div>
    <label className="confirm-check"><input type="checkbox" checked={confirmPermanent} onChange={(event) => onChange(event.target.checked)} /><span className="custom-checkbox">{confirmPermanent && <Check size={12} />}</span><span>我了解这些文件、报告和分析记录将永久删除</span></label>
    <div className="dialog-actions"><button type="button" className="secondary-button" onClick={onCancel}>取消</button><button type="button" className="danger-button" disabled={!confirmPermanent || busy} onClick={onConfirm}>{busy ? <LoaderCircle className="spin" size={15} /> : <Trash2 size={15} />}永久删除</button></div>
  </section></div>;
}

function BatchAnalysisDialog({ packages, busy, onCancel, onConfirm }: { packages: RendererDiagnosticPackage[]; busy: boolean; onCancel: () => void; onConfirm: () => void }) {
  return <div className="modal-backdrop" role="presentation"><section className="confirm-dialog batch-dialog" role="dialog" aria-modal="true" aria-labelledby="batch-dialog-title">
    <div className="dialog-icon batch-dialog-icon"><Play size={21} /></div><div className="dialog-heading"><span className="eyebrow">BATCH ANALYSIS</span><h2 id="batch-dialog-title">分析全部待处理？</h2><p>将为以下 {packages.length} 个诊断包创建分析任务，任务会在右上角任务中心显示进度。</p></div>
    <div className="batch-package-list">{packages.map((item) => <div key={item.id}><FileArchive size={15} /><span title={item.displayName}>{item.displayName}</span></div>)}</div>
    <div className="dialog-actions"><button type="button" className="secondary-button" onClick={onCancel}>取消</button><button type="button" className="primary-button" disabled={busy} onClick={onConfirm}>{busy ? <LoaderCircle className="spin" size={15} /> : <Play size={15} />}开始分析</button></div>
  </section></div>;
}

function TaskDrawer({ open, tasks, onClose, onCancel }: { open: boolean; tasks: TaskRecord[]; onClose: () => void; onCancel: (taskId: string) => void }) {
  return <aside className={`task-drawer ${open ? 'task-drawer-open' : ''}`} aria-label="任务中心" aria-hidden={!open}>
    <div className="drawer-header"><div><span className="eyebrow">WORKBENCH TASKS</span><h2>任务中心</h2></div><button type="button" className="icon-only-button" aria-label="关闭任务中心" onClick={onClose}><X size={18} /></button></div>
    <div className="drawer-summary"><div><strong>{tasks.filter((task) => task.status === 'running').length}</strong><span>进行中</span></div><div><strong>{tasks.filter((task) => task.status === 'succeeded').length}</strong><span>已完成</span></div><div><strong>{tasks.filter((task) => task.status === 'failed').length}</strong><span>失败</span></div></div>
    <div className="task-list">{tasks.length === 0 ? <div className="drawer-empty"><ClipboardList size={24} /><p>暂无分析任务</p></div> : tasks.map((task) => <div className="task-row" key={task.id}><div className="task-row-icon">{task.status === 'running' ? <LoaderCircle className="spin" size={16} /> : task.status === 'succeeded' ? <CheckCircle2 size={16} /> : task.status === 'failed' ? <CircleAlert size={16} /> : <ClipboardList size={16} />}</div><div className="task-row-body"><strong>{task.message || '诊断包分析任务'}</strong><span>{task.status === 'running' ? `分析进度 ${task.progress}%` : task.errorMessage || task.status}</span>{task.status === 'running' && <div className="progress-track"><span style={{ width: `${task.progress}%` }} /></div>}</div>{(task.status === 'running' || task.status === 'queued') && <button type="button" className="task-cancel" aria-label="取消任务" onClick={() => onCancel(task.id)}>取消</button>}</div>)}</div>
  </aside>;
}

function SettingsWindow({ showError, showNotice }: AnalysisCenterProps) {
  const [directories, setDirectories] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法读取设置。');
        setDirectories(await window.workbench.settings.getMonitorDirectories());
      } catch (caught) { showError(caught); }
      finally { setLoading(false); }
    })();
  }, [showError]);

  const chooseDirectory = async () => {
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法选择目录。');
      const path = await window.workbench.settings.chooseMonitorDirectory();
      if (path && !directories.includes(path)) setDirectories((current) => [...current, path]);
    } catch (caught) { showError(caught); }
  };

  const save = async () => {
    setSaving(true);
    try {
      if (!hasWorkbenchBridge()) throw new Error('工作台接口尚未就绪，无法保存设置。');
      await window.workbench.settings.saveMonitorDirectories(directories);
      showNotice('监控目录设置已保存。');
    } catch (caught) { showError(caught); }
    finally { setSaving(false); }
  };

  return <div className="settings-view"><div className="settings-heading"><div className="settings-symbol"><SettingsIcon size={24} /></div><div><span className="eyebrow">PREFERENCES</span><h1>设置</h1><p>管理诊断包自动发现的位置。</p></div></div><div className="settings-section"><div className="settings-section-heading"><div><h2>监控目录</h2><p>分析中心扫描这些目录时，会自动登记新发现的诊断包。</p></div><button type="button" className="secondary-button" onClick={() => void chooseDirectory()}><FolderOpen size={15} />添加目录</button></div>{loading ? <div className="settings-loading"><LoaderCircle className="spin" size={18} />正在读取设置…</div> : directories.length === 0 ? <div className="settings-empty"><Search size={18} /><span>尚未配置监控目录</span></div> : <div className="directory-list">{directories.map((directory) => <div className="directory-row" key={directory}><FolderOpen size={16} /><code>{directory}</code><button type="button" className="icon-only-button" aria-label={`移除监控目录${directory}`} onClick={() => setDirectories((current) => current.filter((item) => item !== directory))}><X size={15} /></button></div>)}</div>}</div><div className="settings-section settings-info"><div className="info-line"><HardDrive size={17} /><span><strong>支持格式</strong><small>.tgz 和 .tgz.temp</small></span></div><div className="info-line"><ShieldCheck size={17} /><span><strong>删除规则</strong><small>删除诊断包会同步清理报告与分析记录</small></span></div></div><div className="settings-actions"><button type="button" className="primary-button" disabled={loading || saving} onClick={() => void save()}>{saving ? <LoaderCircle className="spin" size={15} /> : <Check size={15} />}保存设置</button></div></div>;
}
