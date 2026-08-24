# Hephaestus Workbench v2 模块地图

> 状态日期：2026-08-23。本文描述当前主仓代码，不把后续计划写成已完成功能。

## 1. 解决方案边界

```text
HephaestusWorkbench.App
  → HephaestusWorkbench.Services
  → HephaestusWorkbench.Data
  → HephaestusWorkbench.Core
  → HephaestusWorkbench.PluginSDK
```

- Core：领域模型、仓储契约和可序列化服务请求/结果。
- Data：全新 SQLite schema v2、仓储实现和数据路径。
- Services：初始化、日志监控、分析编排、扩展安装/注册、报告打开和设置。
- App：固定 Shell、Analysis、SSH 占位页面、Extension Center、Settings 和受控 Workspace Host。
- PluginSDK：manifest/catalog v2、Analysis Process、Workspace Bridge 和 Maintenance Workflow DTO。

跨层接口不得暴露 WPF 控件、SQLite Connection、WebView2 对象或具体文件实现。

## 2. Shell 与导航

路径：

- `src/HephaestusWorkbench.App/ViewModels/MainViewModel.cs`
- `src/HephaestusWorkbench.App/ViewModels/ViewModelInfrastructure.cs`
- `src/HephaestusWorkbench.App/MainWindow.xaml`

固定入口：

```text
工作
├─ 分析中心
└─ SSH 终端

扩展
└─ 扩展中心

系统
└─ 设置
```

默认进入分析中心。扩展不能贡献导航。SSH 当前只有界面入口，传输、凭据、Host Key 和 xterm.js 终端尚未实现。

## 3. Workspace 与首次初始化

路径：

- `src/HephaestusWorkbench.Services/WorkspaceVersionGate.cs`
- `WorkbenchInitializationService.cs`
- `WorkbenchConfigurationService.cs`
- `ExtensionSettingsStore.cs`

职责：

- 旧工作区在任何写入前阻断。
- 新工作区使用同盘 staging 创建数据库和三份 schema v2 配置。
- 失败或取消时目标目录保持为空；staging 保守保留并在中文错误中给出路径，由用户手工确认后清理。
- 不迁移、不备份、不递归删除无法证明所有权的数据。

配置：

```text
Config/workspace.json
Config/appsettings.json
Config/extensions.json
```

扩展启用状态、更新通道和默认分析 capability 只由 `ExtensionSettingsStore` 管理。

## 4. Data 与分析生命周期

路径：

- `src/HephaestusWorkbench.Data/DatabaseInitializer.cs`
- `Repositories.cs`
- `AnalysisLifecycleRepository.cs`

当前业务表：

```text
analysis_cases
analysis_tasks
reports
```

报告记录包含 `last_opened_at`。分析生命周期由仓储统一创建和更新；启动时中断的分析任务转换为可诊断状态，不自动重放外部进程。

## 5. 日志收件箱

路径：

- `LogFileParser.cs`
- `ArchiveValidator.cs`
- `LogInboxService.cs`

职责：监控一个或多个目录、识别 `.tgz`/临时日志、解析设备和时间、验证 gzip/tar，并向分析中心发布当前源日志状态。

风险：网络盘、大量文件和未完成写入可能延长刷新；删除源日志必须保留确认。

## 6. Analysis

路径：

- `CaseAnalysisService.cs`
- `AnalysisProcessHost.cs`
- `TaskCenter.cs`
- `RuleSetService.cs`
- `src/HephaestusWorkbench.App/ViewModels/AnalysisCenterViewModel.cs`

流程：

```text
LogInboxItem
→ ExtensionSettingsStore
→ ExtensionRegistry 版本租约
→ 宿主版本 / kind / runtime / protocol / capability 复核
→ Case 与 Task 生命周期落库
→ TaskCenter
→ AnalysisProcessHost (analysis-process-v1)
→ Report/index.html
→ ReportOpenService
→ Windows 默认浏览器
```

分析中心不选择引擎，只选择：

- 综合分析：当前日志分析报告。
- 存储分析：只有扩展声明对应 capability 时才可执行。

零匹配或多匹配不会创建 Case/Task。任务完成、取消和异常均必须释放版本租约；资源释放异常不能阻断队列状态收敛。

## 7. 报告

路径：

- `ReportService.cs`
- `ReportOpenService.cs`
- `src/HephaestusWorkbench.Core/Services/ReportOpenContracts.cs`

报告入口固定为 Case 解压目录内的 `Report/index.html`。打开前依次校验报告目录、入口路径、Case 边界和文件存在性，再调用 Windows Shell 默认浏览器并更新 `last_opened_at`。

App 不包含报告 Tab 或嵌入式报告 WebView2。WebView2 依赖只为 Workspace Host 保留，后续还将用于 SSH 终端。

## 8. Extension manifest/catalog v2

路径：

- `src/HephaestusWorkbench.PluginSDK/ExtensionManifest.cs`
- `ExtensionCatalog.cs`
- `ExtensionContractValidator.cs`
- `src/HephaestusWorkbench.Services/ExtensionCatalogClient.cs`
- `ExtensionPackageVerifier.cs`
- `ExtensionInstaller.cs`
- `ExtensionRegistry.cs`
- `ExtensionCenterService.cs`

职责：

- 校验 kind/runtime/capability/permission/Host API。
- 校验下载大小、SHA-256、Ed25519、发布者信任和 ZIP 路径。
- 同盘 staging 安装、`current.json` pending/healthy、加载失败回滚和版本租约。
- 扩展中心展示发现、已安装和更新，并阻止身份冲突或宿主不兼容版本。

当前生产组合根的信任表为空，因此新包安装 fail-closed。正式信任锚和离线 BundledExtensions 尚未实现。

## 9. Workspace Host

路径：

- `src/HephaestusWorkbench.App/Views/WorkspaceHostWindow.xaml.cs`
- `WorkspaceBrowserSecurityPolicy.cs`
- `src/HephaestusWorkbench.PluginSDK/WorkspaceBridgeProtocol.cs`

Workspace 页面只从扩展中心打开。Host 使用独立 WebView2 profile、固定虚拟来源和目录映射；禁止网络、跨源导航、新窗口、下载、外部协议、默认对话框、外部拖放和未授权权限。

Web Message 必须同源并符合版本化 JSON-RPC。当前没有获批的 Bridge 方法。窗口持有具体扩展版本租约，并在浏览器清理失败时仍通过 `finally` 释放。

## 10. PluginSDK

路径：`src/HephaestusWorkbench.PluginSDK`

当前文件：

- `ExtensionManifest.cs`
- `ExtensionCatalog.cs`
- `AnalysisProcessProtocol.cs`
- `WorkspaceBridgeProtocol.cs`
- `MaintenanceWorkflowProtocol.cs`

SDK 只包含 JSON 可序列化 DTO 和验证器，不包含第三方 DLL/WPF 接口。Analysis Process 与 Workspace Bridge 已接入宿主；Analysis Content 只有 manifest 组合校验，没有内容 DTO/schema；Maintenance 已有预留 DTO，但 Planner/Policy/Executor 尚未实现。

## 11. Settings

路径：

- `SettingsService.cs`
- `SettingsViewModel.cs`
- `SettingsPage.xaml`

当前持久化偏好包括主题、清理保留天数和工作区监控目录。SSH、扩展策略、存储和关于分区仍需按正式版计划继续收口。

## 12. 尚未实现的正式版阶段

- SSH.NET 交互终端与独立命令执行通道。
- Windows Credential Manager、Host Key TOFU、重连和 xterm.js 背压。
- Maintenance Catalog、Planner、Policy、Executor、操作历史和 `OutcomeUnknown`。
- Analysis Content 的内容 DTO/schema 与宿主应用流程；Maintenance Content 的加载与执行框架。
- `BundledExtensions/`、锁定清单、正式信任锚和统一离线安装事务。
- 安装器与 Release Workflow 的 v2 改造以及 Windows 10/11 手工烟测。

## 13. 验证命令

```powershell
dotnet restore .\HephaestusWorkbench.sln --configfile .\NuGet.config
dotnet build .\HephaestusWorkbench.sln -c Release --no-restore
dotnet test .\HephaestusWorkbench.sln -c Release --no-build --no-restore
git diff --check
```

关键类补充中文设计注释；用户可见错误和日志使用明确中文。
