# 赫菲斯托斯工程工作台（Hephaestus Workbench）

赫菲斯托斯工程工作台（简称“赫工”）是面向内部工程师的 Windows 本地诊断包分析工作台，当前唯一运行时为 Electron。

## 当前能力

- 监控一个或多个本地目录，自动发现 `.tgz` 和 `.tgz.temp` 诊断包。
- 支持文件选择和拖放导入诊断包。
- 使用内置 TypeScript 分析规则执行综合分析和存储健康分析。
- 管理分析任务、报告路径、解压目录和诊断包生命周期。
- 在单个 Electron 窗口内提供桌面、应用窗口、任务中心和设置页面。
- 桌面应用图标拖动时自动吸附到网格，并持久化图标布局。

## 开发环境

- Windows 10/11 x64
- Node.js 与 npm
- Electron 35

## 构建与运行

```powershell
npm ci
npm run dev
```

常用检查：

```powershell
npm run typecheck
npm test
npm run build
```

## 制作 Windows 安装包

Electron Windows 安装包由 electron-builder 生成，安装包自带运行所需组件。版本唯一来自 `package.json` 的 `version` 字段。

```powershell
npm run package:win
```

输出位于 `release`，安装包名称为 `HephaestusWorkbench_v<版本号>.exe`。安装器支持选择安装目录、创建桌面和开始菜单快捷方式，以及后续升级和卸载；卸载默认保留用户数据。

## 用户数据

主进程通过 Electron `userData` 目录保存工作台数据，诊断包、任务、桌面布局和监控目录均由主进程仓储管理。渲染进程只能通过 preload 暴露的 `window.workbench` 接口访问本地能力。

## 发布

GitHub Actions 使用 `npm ci`、类型检查、Vitest、Electron 构建和 Windows 安装包构建完成发布验收，并生成 SHA-256 校验文件。发布流程见 [Electron 分发说明](docs/distribution.md)。历史版本说明保留在 `distribution/releases`。

## 验收流程

1. 启动开发环境并进入分析中心。
2. 在设置中添加监控目录，或拖入一个 `.tgz` / `.tgz.temp` 文件。
3. 确认诊断包进入列表后启动综合分析或存储健康分析。
4. 从任务中心观察进度，从诊断包卡片打开报告或定位文件。
5. 拖动桌面应用图标，确认图标实时吸附到统一网格并在重启后保持位置。

错误信息和任务失败原因会以中文提示，便于非开发者部署和排查。
