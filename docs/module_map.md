# Workbench 模块地图

Workbench 只描述宿主模块；应用源码和应用业务模块位于 `Workbench-Apps`。

## 系统依赖关系

```text
Electron 主进程
 ├── IPC 注册层
 │    ├── DesktopLayoutRepository ──> userData/Workbench/workbench.db
 │    ├── AppCatalogClient ──> Workbench-Apps/catalog.json
 │    ├── AppPackageInstaller ──> 应用 ZIP 校验与安装
 │    └── AppRuntimeManager ──> 应用 backend Worker
 ├── RulesService ──> rules.* Host API
 └── Seed App Fetcher ──> Workbench-Apps 分析中心正式 Release

Preload ──> contextBridge ──> Renderer React Desktop Shell
Renderer ──> window.workbench ──> IPC / App Host RPC
electron-builder ──> Windows NSIS 安装包
```

## 模块边界

| 模块 | 主要路径 | 职责 |
| --- | --- | --- |
| Electron 主进程 | `src/main/index.ts` | 创建窗口、加载 renderer、管理应用生命周期 |
| IPC | `src/main/ipc.ts` | 校验输入，暴露桌面、应用中心、Host API 和规则接口 |
| 桌面布局 | `src/main/data/desktop-layout-repository.ts` | 只保存宿主桌面图标布局 |
| 应用中心 | `src/main/services/app-*` | 读取目录、安装应用、校验包、启动和停止 backend |
| 规则 Host API | `src/main/services/rules-service.ts` | 管理官方规则、用户增量、激活快照和导出/提交结果 |
| Preload | `src/preload/index.ts` | 以最小白名单 API 暴露宿主能力 |
| React 桌面 | `src/renderer/src/App.tsx` | 桌面图标、应用窗口、任务抽屉和通用应用 RPC |
| 发布 | `package.json`、`.github/workflows/release.yml` | 下载种子包、构建安装包和发布 SHA-256 资产 |

## 应用边界

`Workbench-Apps` 负责以下应用及其独立数据：

- `analysis-center`：诊断包导入、日志规则分析、结构化存储分析和离线报告；
- `lvm-uncache-tool`：LVM 文本转换与安全保存；
- `terminal`：SSH 连接和凭据 Host API；
- `log-rule-editor`：用户规则读取、校验、保存、提交和导出。

应用不得引用 Workbench 主进程内部模块，只能通过版本化 App Host API 通信。
