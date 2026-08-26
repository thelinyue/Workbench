# Workbench 项目上下文

## 当前架构

本分支是纯 Electron 应用，运行时只有一个 Electron `BrowserWindow`：

```text
src/main/index.ts
  ├── src/main/ipc.ts
  ├── src/main/data/workspace-repository.ts
  ├── src/main/services/*
  └── src/main/analysis/*

src/preload/index.ts
  └── window.workbench

src/renderer/src/App.tsx
  └── React Desktop Shell、虚拟窗口、分析中心、设置和任务中心
```

旧的 .NET/WPF、C# 项目、WebView2、Inno 安装器和外部插件注入链路不属于当前分支，不应重新添加为运行依赖。

## 运行边界

- 主进程负责 SQLite、文件系统、目录监控、诊断包分析、任务和删除操作。
- Renderer 不访问 Node 或文件系统，只能调用 preload 暴露的 `window.workbench`。
- IPC 所有输入在主进程使用 Zod 校验。
- 诊断包分析使用 `src/main/analysis` 中的内置 TypeScript 逻辑和规则。
- 用户数据保存在 Electron `userData/Workbench` 目录，数据库由 `WorkspaceRepository` 管理。
- 日志和错误信息使用中文，便于非开发者部署和排查。

## 关键入口

| 能力 | 入口 |
| --- | --- |
| Electron 生命周期 | `src/main/index.ts` |
| IPC 和安全校验 | `src/main/ipc.ts` |
| SQLite 仓储 | `src/main/data/workspace-repository.ts` |
| 分析服务 | `src/main/services/analysis-center-service.ts` |
| 分析任务 | `src/main/services/analysis-task-service.ts` |
| 删除生命周期 | `src/main/services/lifecycle-deletion-service.ts` |
| React 桌面 | `src/renderer/src/App.tsx` |
| 桌面图标网格 | `src/renderer/desktop-layout.ts` |
| Preload API | `src/preload/index.ts` |
| 类型声明 | `src/shared/bridge.d.ts` |

## 桌面图标布局

桌面应用图标使用固定槽位网格：横向 116px、纵向 142px，坐标原点为 `(44, 96)`。拖动时实时吸附；启动加载历史坐标时归一化并回写数据库。不要把虚拟应用窗口位置和桌面图标位置混为一套布局。

## 构建与发布

```powershell
npm ci
npm run typecheck
npm test
npm run build
npm run package:win
```

Windows 安装包由 electron-builder NSIS 生成到 `release`，版本唯一来自 `package.json.version`。CI 工作流位于 `.github/workflows/release.yml`，发布资产只包含安装包和 SHA-256 校验文件。

## 修改规则

- 修改 IPC 时同步更新 preload、`src/shared/bridge.d.ts` 和测试。
- 修改 SQLite schema 时提供兼容迁移，不删除用户数据。
- 新增行为先写失败测试，再实现最小代码。
- 不重置或覆盖工作区中与当前任务无关的用户改动。
- 完成前运行类型检查、完整测试和构建；不得只凭代码阅读声称修复完成。
