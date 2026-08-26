# Workbench 项目上下文

## 当前架构

本仓库是 Electron 宿主，运行时只有一个 Electron `BrowserWindow`。应用源码和应用业务 backend 位于独立的 `Workbench-Apps` 仓库。

```text
src/main/index.ts
  ├── src/main/ipc.ts
  ├── src/main/data/desktop-layout-repository.ts
  ├── src/main/services/app-*
  ├── src/main/services/rules-service.ts
  └── src/main/services/app-backend-worker.ts

src/preload/index.ts
  └── window.workbench

src/renderer/src/App.tsx
  └── React Desktop Shell、虚拟应用窗口、任务中心
```

## 运行边界

- 宿主负责桌面布局、应用目录、安装、运行时和版本化 App Host API。
- `Workbench-Apps` 负责分析中心、LVM 工具、SSH 终端和规则编辑器的源码与独立数据。
- Renderer 不访问 Node 或文件系统，只能调用 preload 暴露的 `window.workbench`。
- IPC 所有输入在主进程使用 Zod 校验；应用只能通过 Host API 使用受授权能力。
- 宿主数据库只保存桌面布局；应用数据库由应用 backend 自己管理。
- 日志和错误信息使用中文，便于非开发者部署和排查。

## 关键入口

| 能力 | 入口 |
| --- | --- |
| Electron 生命周期 | `src/main/index.ts` |
| IPC 和安全校验 | `src/main/ipc.ts` |
| 桌面布局仓储 | `src/main/data/desktop-layout-repository.ts` |
| 应用目录与安装 | `src/main/services/app-*` |
| 规则 Host API | `src/main/services/rules-service.ts` |
| React 桌面 | `src/renderer/src/App.tsx` |
| 桌面图标网格 | `src/renderer/desktop-layout.ts` |
| Preload API | `src/preload/index.ts` |
| 类型声明 | `src/shared/bridge.d.ts` |

## 构建与发布

```powershell
npm ci
npm run typecheck
npm test
npm run build
npm run package:win
```

打包前会从 `Workbench-Apps` 正式 Release 下载并校验分析中心种子包。CI 工作流位于 `.github/workflows/release.yml`，发布资产只包含安装包和 SHA-256 校验文件，并发布到当前 Workbench 仓库。

## 修改规则

- 修改 IPC 时同步更新 preload、`src/shared/bridge.d.ts` 和测试。
- 修改 App Host 协议时同步更新 `Workbench-Apps` 应用与 manifest。
- 修改 SQLite schema 时提供兼容迁移，不删除用户数据。
- 不重置或覆盖工作区中与当前任务无关的用户改动。
- 完成前运行类型检查、完整测试和构建；不得只凭代码阅读声称修复完成。
