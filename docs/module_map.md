# Workbench 模块地图

> 当前基线：Electron + React。本文只描述当前源码中存在的运行模块，不把历史实现或未实现能力当作产品入口。

## 1. 系统依赖关系

```text
Electron 主进程
 ├── IPC 注册层
 │    ├── WorkspaceRepository ──> userData/Workbench/workbench.db
 │    ├── AnalysisCenterService
 │    ├── AnalysisTaskService
 │    ├── LifecycleDeletionService
 │    └── MonitorDirectoryWatcher
 └── analysis-worker ──> 内置 TypeScript 分析规则

Preload ──> contextBridge ──> Renderer React Desktop Shell
Renderer ──> window.workbench ──> IPC
Renderer ──> 本地报告文件由系统默认程序打开
electron-builder ──> Windows NSIS 安装包
```

## 2. 模块边界

| 模块 | 主要路径 | 职责 |
| --- | --- | --- |
| Electron 主进程 | `src/main/index.ts` | 创建单个 BrowserWindow、加载 renderer、管理应用生命周期 |
| IPC | `src/main/ipc.ts` | 校验渲染层输入，暴露桌面、设置、分析、任务和删除操作 |
| 本地仓储 | `src/main/data/workspace-repository.ts` | 通过 Node `node:sqlite` 保存诊断包、任务、报告索引、监控目录和桌面布局 |
| 分析服务 | `src/main/services`、`src/main/analysis` | 扫描/导入诊断包、解压、执行内置规则、生成报告和跟踪进度 |
| Preload | `src/preload/index.ts` | 以最小白名单 API 将 IPC 能力安全暴露给 renderer |
| React 桌面 | `src/renderer/src/App.tsx` | 桌面图标、虚拟应用窗口、任务抽屉、分析中心和设置页面 |
| 桌面布局 | `src/renderer/desktop-layout.ts` | 定义图标网格、实时吸附和历史坐标归一化 |
| 共享契约 | `src/shared/bridge.d.ts` | 声明 `window.workbench` 的类型和 IPC 返回结构 |
| 发布 | `package.json`、`.github/workflows/release.yml` | Electron 构建、Windows 安装包和 SHA-256 资产发布 |

## 3. 数据流

### 诊断包到报告

```text
监控目录/拖放文件
  → MonitorDirectoryWatcher / IPC
  → AnalysisCenterService
  → WorkspaceRepository
  → AnalysisTaskService
  → analysis-worker
  → report.html 与分析状态
  → Renderer 刷新列表并允许打开报告
```

### 桌面图标布局

```text
Renderer 加载布局
  → normalizeDesktopLayout
  → 必要时通过 desktop:save-layout 回写
  → PointerMove 使用 snapDesktopIconPoint
  → PointerUp 持久化网格坐标
```

## 4. 安全边界

- Renderer 不访问 Node、文件系统或 SQLite，只调用 preload API。
- IPC 主进程使用 Zod 校验 ID、路径列表、删除确认令牌和桌面坐标。
- 删除操作必须先生成预览，再使用短期确认令牌执行。
- 报告和诊断包路径由主进程根据仓储对象解析，不信任 renderer 直接传入的任意路径。
- 日志和错误信息使用中文，便于部署者排查。

## 5. 测试与维护规则

- `tests/main` 覆盖仓储、分析、删除和目录服务。
- `tests/renderer` 覆盖 UI 源码约束、窗口管理器、桌面布局和交互回归。
- 修改 IPC 契约时同步更新 `src/shared/bridge.d.ts`、preload 和测试。
- 修改数据库结构时必须提供兼容迁移并验证已有用户数据。
- 新增 UI 交互时必须处理键盘/辅助技术语义，避免把拖拽作为唯一操作方式。
