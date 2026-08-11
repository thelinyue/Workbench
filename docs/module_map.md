# Hephaestus Workbench 模块地图

> 基线日期：2026-08-11  
> 用途：维护当前模块边界、入口、依赖和风险。新增或修改功能前，应先阅读本文件和 `.codex/project_context.md`。

## 1. 系统依赖关系

```text
App/UI
 ├── Core
 ├── Services
 │    ├── Core
 │    ├── Data
 │    └── PluginSDK
 ├── Data
 └── PluginSDK

Services
 ├── SQLite repositories ──> Data ──> workbench.db
 ├── Configuration ────────> JSON files
 ├── Inbox ────────────────> FileSystemWatcher + .tgz files
 ├── Analysis ─────────────> TaskCenter + Plugin runners
 ├── Plugin runners ───────> external EXE + report files
 └── Report/Storage ───────> repositories + Case directories

Report UI ──> WebView2 ──> local report.html
Installer ──> published App payload; does not own user Case data
SSH ──> not implemented
```

## 2. 模块边界总表

| 模块 | 数据库 | JSON 配置 | 用户文件 | 外部进程 | UI 状态 |
| --- | --- | --- | --- | --- | --- |
| App/UI | 不直接访问 | 不直接访问 | 通过 Services 间接访问 | 不直接启动 | 负责导航和绑定 |
| Core | 不访问 | 不访问 | 不访问 | 不访问 | 无 |
| Data | 唯一正式访问层 | 不负责 | 创建数据目录 | 不负责 | 无 |
| Configuration/Settings | 兼容读取 `app_settings` | 读写三个配置文件 | 创建配置目录 | 不负责 | 通过事件通知 |
| Log Inbox | 不落库 | 通过配置服务保存目录 | 读取/删除监控目录中的 `.tgz` | 不负责 | 内存 `LogInboxItem` |
| Case Analysis | 写 Case/Task/Report | 读取插件目录结果 | 创建/删除 Case 报告目录，管理原始日志路径 | 通过 runner 启动 | `StateChanged` |
| Task Center | 写入由 Analysis 完成 | 不访问 | 不直接访问 | 管理取消令牌 | `TaskChanged` |
| Plugin | 同步 plugin_info | 读在线目录和 manifest，写默认/启用配置 | 安全安装、更新和卸载 Plugins 目录 | 启动 EXE | 在线目录、安装状态、Issues |
| Report | 查询 reports/session/case | 读取报告偏好 | 读取 `report.html` | WebView2 宿主进程 | Tab、筛选、阅读位置 |
| Storage | 读取 Case | 不访问 | 统计/删除日志和 Extract | 不负责 | 占用统计 |
| Installer | 不访问运行时数据库 | 不访问运行时配置 | 安装目录、Payload、升级备份 | 发布/运行安装器 | 安装器窗口 |
| Legacy Script | 不访问 WPF 数据库 | 自有脚本配置 | 直接处理日志和配置 | 可生成 SSH 命令 | 控制台交互 |
| SSH | 无 | 无 | 无 | 无 | 未实现 |

## 3. App Shell 与 UI 模块

名称：App Shell、Views、ViewModels  
路径：`src/HephaestusWorkbench.App`  
职责：负责 WPF 启动、首次运行窗口、主窗口、顶部状态、导航、页面展示、主题和 WebView2 控件生命周期；不承载数据库 SQL、Case 文件操作或插件命令行拼接。  
入口文件：

- `App.xaml.cs`：`App.OnStartup`、`WorkbenchHost.CreateAsync`、`WorkbenchHost.InitializeAsync`
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `ViewModels/MainViewModel.cs`
- `Views/ReportViewerControl.xaml.cs`

依赖：WPF、Windows Forms 对话框、WebView2、Core、Data、Services、PluginSDK；项目声明 `CommunityToolkit.Mvvm`，当前主要使用自定义 ViewModel 基础设施。  
输入：用户命令、绑定属性、Services 事件、初始化结果和报告文件路径。  
输出：页面状态、导航状态、MessageBox、主题资源、报告 WebView2 视图。  
风险点：

- WPF UI 线程和后台事件之间需要正确切换。
- `async void` 事件、页面卸载和 WebView2 Dispose 顺序容易造成未观察异常或资源泄漏。
- UI 不能绕过 Services 直接修改业务数据。

## 4. Core 模块

名称：Core Models 与 Repository Interfaces  
路径：`src/HephaestusWorkbench.Core`  
职责：定义 `AnalysisCase`、`AnalysisTask`、`Report`、`ReportSummary`、`ReportSession`、`PluginInfo`、`LogInboxItem`、配置模型、状态枚举以及仓储接口。  
入口文件：

- `Models/*.cs`
- `Repositories/IRepositories.cs`

依赖：仅 .NET 基础库，不依赖 WPF、Data、Services 或具体数据库。  
输入：业务层构造的模型和查询条件。  
输出：稳定的领域数据结构和持久化抽象。  
风险点：

- 模型字段、状态枚举和仓储接口是跨层契约，改动需要同步 Data、Services、UI 和测试。
- 状态字符串存入 SQLite，新增/重命名状态必须考虑旧数据迁移。

## 5. Data 与 SQLite 模块

名称：Data、SQLite 初始化与仓储  
路径：`src/HephaestusWorkbench.Data`  
职责：管理用户数据根目录、SQLite 连接、数据库初始化/迁移以及 Case、Task、Report、ReportSession、PluginInfo、Settings 仓储。  
入口文件：

- `DataPaths.cs`
- `SqliteConnectionFactory.cs`
- `DatabaseInitializer.cs`
- `Sqlite*Repository.cs`

依赖：Core、`Microsoft.Data.Sqlite` 8.0.8。  
输入：Core 模型、`ReportQuery`、数据根目录和取消令牌。  
输出：SQLite `workbench.db`、仓储查询结果、配置目录路径和 Case 路径。  
风险点：

- schema 迁移必须幂等且不能覆盖用户历史数据。
- `plugin_info` 已有表和仓储，但生产启动流程没有完整登记发现结果。
- 报告查询依赖 `plugin_info` 名称回填，数据缺失时会显示插件 ID 或“未知插件”。
- `ReportsDirectory` 已被创建，但当前 Case 报告主要位于 `Cases/<CaseId>/Report`，需要保持目录语义一致。

## 6. Configuration、Settings 与 Initialization 模块

名称：配置、设置和首次初始化  
路径：`src/HephaestusWorkbench.Services/WorkbenchConfigurationService.cs`、`SettingsService.cs`、`WorkbenchInitializationService.cs`  
职责：创建数据目录和数据库，读取/规范化/原子写入 JSON，迁移旧 SQLite 设置，维护监控目录、主题、报告 Tab 上限、恢复开关和插件配置。  
入口文件：

- `WorkbenchInitializationService.InitializeAsync`
- `WorkbenchConfigurationService.EnsureWorkspaceAsync`
- `WorkbenchConfigurationService.EnsureAppSettingsAsync`
- `SettingsService`

依赖：Core、Data、PluginProvisioningService、PluginCatalog、WorkbenchLogger。  
输入：用户选择的数据根目录、监控目录、旧版 SQLite 键值和设置页面输入。  
输出：数据目录、`workbench.db`、三个 JSON 配置、内置插件目录和初始化进度。  
风险点：

- 配置文件格式错误会阻断初始化，需要保留明确中文错误。
- JSON 与旧 SQLite 设置存在双写/兼容镜像，必须明确单一事实来源。
- 数据目录不能位于程序安装目录；修改路径检查时不能破坏升级和用户数据隔离。

## 7. Log Inbox 模块

名称：日志收件箱  
路径：`src/HephaestusWorkbench.Services/LogFileParser.cs`、`ArchiveValidator.cs`、`LogInboxService.cs`  
职责：监控一个或多个目录，识别命名规则符合的 `.tgz` 文件，解析设备 ID 和日志时间，校验 gzip/tar，并通过事件通知 UI。  
入口文件：

- `LogInboxService.StartAsync`
- `LogInboxService.RefreshAsync`
- `LogFileParser.TryParse`
- `ArchiveValidator.ValidateAsync`

依赖：Core、Data 配置接口、`FileSystemWatcher`、`System.Formats.Tar`、gzip/文件系统 API。  
输入：`workspace.json` 的 `MonitorPaths`、目录变化事件和用户刷新/删除命令。  
输出：内存 `LogInboxItem` 列表、`ItemsChanged`、`ConfigurationChanged` 事件、日志文件删除。  
风险点：

- 解析失败的文件名会被过滤，不会以无效项目展示在收件箱中。
- watcher 事件可能重复或在文件仍未写完时触发；当前通过稳定性等待和刷新锁缓解。
- 逐文件校验大压缩包或网络盘可能延长启动和刷新。
- 删除操作不可恢复，UI 必须保留确认提示。

## 8. Case Analysis 模块

名称：案例分析服务  
路径：`src/HephaestusWorkbench.Services/CaseAnalysisService.cs`  
职责：把收件箱文件转成 Case 和 Task，创建 Case 目录，选择插件 runner，执行分析，写入报告并更新生命周期状态。  
入口文件：

- `CaseAnalysisService.StartAsync`
- `CaseAnalysisService.CancelAsync`
- `CaseAnalysisService.RenameAsync`
- `CaseAnalysisService.DeleteAsync`

依赖：Core、Data repositories、DataPaths、TaskCenter、PluginCatalog、Plugin runners、WorkbenchLogger。  
输入：已校验的 `LogInboxItem`、取消令牌、用户重命名/删除命令。  
输出：`analysis_cases`、`analysis_tasks`、`reports` 记录，原始日志路径和 Case Report 目录，`StateChanged` 事件。
风险点：

- 写 Case、写 Task、启动插件和写 Report 不是一个事务，失败时可能留下原始路径中的部分解压数据。
- 当前通过 fire-and-forget 入队；数据库/文件异常需要保证被记录并转换为可理解的失败状态。
- 应用重启后 Waiting/Running 任务没有恢复/重试协议。
- 插件选择必须使用 `plugins.json` 中已启用的默认插件；默认项缺失时不得静默切换到其他插件。

## 9. Task Center 模块

名称：后台任务中心  
路径：`src/HephaestusWorkbench.Services/TaskCenter.cs`  
职责：维护内存取消令牌、最多两个并行插件执行槽位和任务完成通知。  
入口文件：`TaskCenter.EnqueueAsync`、`TaskCenter.Cancel`。  
依赖：Core 的 `AnalysisTask`，`SemaphoreSlim`，内存字典。  
输入：AnalysisTask、异步执行委托、取消命令。  
输出：执行委托运行、取消信号、`TaskChanged` 事件。  
风险点：

- 队列和取消状态不持久化。
- 任务动作异常的观察和状态回写必须由上层设计保证。
- 并行数是当前固定值 2，不应在未验证资源模型前随意调整。

## 10. Plugin Catalog、Provisioning 与 Runtime 模块

名称：插件目录、内置插件供应和运行时  
路径：

- `src/HephaestusWorkbench.Services/PluginCatalog.cs`
- `PluginProvisioningService.cs`
- `ProcessPluginRunners.cs`
- `src/HephaestusWorkbench.PluginSDK/PluginContracts.cs`

职责：扫描 `manifest.json`，校验入口存在，复制内置 `log_analyzer.exe`，根据 manifest 选择 legacy/standard runner，并通过外部进程生成报告。  
入口文件：

- `PluginCatalog.ScanAsync`
- `PluginProvisioningService.ProvisionAsync`
- `LegacyLogAnalyzerRunner.RunAsync`
- `StandardExePluginRunner.RunAsync`

依赖：Core、DataPaths、PluginSDK、`System.Diagnostics.Process`、文件系统。  
输入：插件目录、manifest、Case Source/Extract/Report 路径、取消令牌。  
输出：PluginManifest、Issues、外部 EXE 进程结果、`report.html`、插件日志。  
风险点：

- manifest、在线目录、压缩包和 HTML 输出属于外部信任边界；v1.1.0 使用 HTTPS、SHA-256、大小和路径边界校验，但尚未提供数字签名或沙箱。
- `PluginType.Dll` 和 `IAnalysisPlugin` 只是契约，当前没有 DLL 加载实现。
- `plugins.json` 保存安装来源、启用状态和默认插件，运行、更新与卸载必须遵守这些状态。
- 内置 EXE 仅在随应用版本更高或目标文件缺失时更新，不能覆盖在线安装的更高版本。
- legacy runner 保留旧程序的 `-d` 参数，并新增 `-o` 报告输出目录；解压内容留在原始日志目录，工作台只保存报告目录。

## 11. PluginSDK 模块

名称：插件 SDK 和运行契约  
路径：`src/HephaestusWorkbench.PluginSDK`  
职责：定义 `PluginType`、`PluginManifest`、`PluginExecutionContext`、`PluginExecutionResult`、`IAnalysisPlugin`、`IPluginCatalog` 和 `IPluginRunner`。  
入口文件：`PluginContracts.cs`。  
依赖：Core、.NET 基础库。  
输入：插件清单和执行上下文。  
输出：供 Services 和未来第三方插件使用的公共契约。  
风险点：

- 这是跨进程/跨版本边界，字段改动必须考虑旧插件和 manifest 兼容。
- 不要把 WPF 类型、数据库连接或 UI 状态加入 SDK。
- 当前 DLL 接口没有对应生产 runner，文档和产品界面不能宣称已支持 DLL 插件。

## 12. Report Service 与 Report Workspace 模块

名称：报告服务、报告库、Tab 工作区和查看器  
路径：

- `src/HephaestusWorkbench.Services/ReportService.cs`
- `src/HephaestusWorkbench.App/ViewModels/ReportsViewModel.cs`
- `ReportsWorkspaceViewModel.cs`
- `ReportTabViewModel.cs`
- `src/HephaestusWorkbench.App/Views/ReportPage.xaml.cs`
- `ReportViewerControl.xaml.cs`

职责：查询报告、按关键字/设备/插件/日期筛选、打开和关闭报告 Tab、保存恢复状态，并使用 WebView2 加载只读 `report.html`。  
入口文件：`ReportService.ListAsync`、`ReportsWorkspaceViewModel.InitializeAsync`、`OpenReportAsync`、`ReportViewerControl.OnLoaded`。  
依赖：Core、Data repositories、SettingsService、CaseAnalysisService、WebView2。  
输入：`ReportQuery`、ReportSession、报告目录、用户筛选和 Tab 操作。  
输出：`ReportSummary` 列表、`ReportSession` 持久化、WebView2 内容和滚动位置。  
风险点：

- 当前最多打开 10 个报告，设置值和活动 Tab 状态需要保持一致。
- 报告中心删除报告的产品语义是删除所属 Case 及全部数据，不只是删除 HTML。
- 本地 HTML 由插件生成，报告内容的信任边界需要明确。
- 报告文件丢失时必须保持非阻断提示和可恢复的报告库状态。

## 13. Storage 模块

名称：存储统计与清理  
路径：`src/HephaestusWorkbench.Services/StorageService.cs`、`src/HephaestusWorkbench.App/ViewModels/StorageViewModel.cs`  
职责：统计数据根目录、日志、Extract、Report 的空间占用，并按 Case 删除原始日志和 Extract。  
入口文件：`StorageService.GetSummaryAsync`、`StorageService.CleanCaseDataAsync`。  
依赖：DataPaths、Case repository、FileUtilities。  
输入：数据根目录、Case ID、用户确认。  
输出：`StorageSummary`、删除后的文件系统状态和 UI 统计。  
风险点：

- 清理只保留报告，用户必须能明确看到删除/保留边界。
- 目录统计可能受大文件、网络路径、访问权限和文件并发变化影响。
- `FileUtilities.DeleteDirectoryIfExists` 是递归删除，调用前必须严格校验路径来源。

## 14. Logger 模块

名称：工作台日志  
路径：`src/HephaestusWorkbench.Services/WorkbenchLogger.cs`  
职责：将中文信息/错误写入 `Logs/workbench.log`，并通过事件把最近日志推送到 UI 状态栏。  
入口文件：`WorkbenchLogger.Info`、`WorkbenchLogger.Error`。  
依赖：用户数据目录和文件系统。  
输入：业务事件、异常和中文消息。  
输出：日志文件、`MessageWritten` 事件。  
风险点：

- 日志写入异常可能影响错误处理链路，需要避免覆盖原始异常。
- 当前没有日志轮转、大小上限或结构化字段，长期运行可能导致日志增长。
- 日志内容应避免写入密码、私钥和不必要的敏感数据。

## 15. Installer 模块

名称：Windows 安装器、升级和卸载  
路径：`installer/HephaestusWorkbench.Setup`、`installer/build-installer.ps1`  
职责：发布 .NET 8 self-contained win-x64 应用，生成 Payload，提供安装、升级、卸载入口，检查 WebView2 和安装目录，并在升级前备份用户数据。  
入口文件：

- `installer/build-installer.ps1`
- `HephaestusWorkbench.Setup/Program.cs`
- `InstallOperations.cs`
- `InstallPathForm.cs`

依赖：.NET 8 Windows Forms、`Payload.zip`、Windows 文件系统、WebView2 Runtime（目标机前置条件）。  
输入：Configuration、Version、安装路径、Payload 和可选离线 WebView2 安装器。  
输出：`installer/dist/HephaestusWorkbench_Setup_v<版本号>.exe`、Windows 卸载入口和安装目录。
风险点：

- 安装器输出和 Payload 属于生成物，不应被误当作源码模块。
- 卸载默认保留用户数据，任何数据删除必须明确确认并验证目标路径。
- self-contained 不等于自带 WebView2 Runtime，安装/运行环境检查必须如实提示。
- 安装路径、旧版本迁移和用户数据目录不能发生误覆盖。

## 16. Tests 模块

名称：自动化测试  
路径：`tests/HephaestusWorkbench.Tests`  
职责：覆盖路径、安装器、日志解析/收件箱、插件 manifest/catalog、SQLite 仓储、配置和报告工作区。  
入口文件：各 `*Tests.cs`，项目入口为 `HephaestusWorkbench.Tests.csproj`。  
依赖：Core、Data、Services、App、Setup、xUnit、Microsoft.NET.Test.Sdk。  
输入：临时目录、内存 Settings Store、测试 Case/Report/manifest。  
输出：测试结果和临时文件。  
风险点：

- 当前测试主要验证本地逻辑，不能替代真实 WebView2、外部插件、网络盘、安装升级和权限场景。
- UI/异步事件测试需要注意 Dispatcher 和资源释放。
- 修改 schema、插件协议或删除语义时，必须同步扩展回归测试。

## 17. 遗留 PowerShell 模块

名称：旧版日志管理脚本  
路径：`初始脚本/logtool.ps1`  
职责：旧版控制台工具，包含日志解压、SSH 命令生成、LVM 缓存处理、按天清理、配置和报告查找。  
入口文件：脚本末尾主菜单循环。  
依赖：PowerShell、外部解压工具/现有插件路径约定。  
输入：控制台输入、日志目录、脚本配置。  
输出：解压文件、清理结果、SSH 命令文本和控制台日志。  
风险点：

- 含永久删除和 SSH 命令生成逻辑，不具备 WPF 应用的服务边界、凭据安全和 UI 确认语义。
- 不应直接复制脚本代码实现新的桌面端 SSH 或清理功能。
- 该脚本只作为历史资产和兼容背景记录。

## 18. 插件及内置资产模块

名称：内置日志分析插件和测试日志资产  
路径：`插件/log_analyzer.exe`、`插件/宇diag_EC660JJ42230BE31_2608101025.tgz`、`src/HephaestusWorkbench.App/PluginSeed/manifest.json`。  
职责：提供当前 MVP 使用的旧版日志分析可执行文件、manifest 和验收样例。  
入口文件：manifest 中的 `entry`，内置入口为 `log_analyzer.exe`；legacy runner 使用 `-d <原始日志文件> -o <报告目录>`。
依赖：Windows x64、插件自身运行环境、原始日志目录和 Case Report 目录约定。
输入：`.tgz` 日志文件和 `-d`/标准 runner 参数。  
输出：原始输入文件同名目录中的解压内容，以及工作台 Case Report 目录中的完整报告资源。
风险点：

- 二进制资产不等同于可审计的源代码，更新和来源需要单独管理。
- 插件失败、报告缺失、进程被取消或输出目录异常必须形成可理解的 Task/Case 状态。
- manifest、二进制和输出报告都属于插件信任边界。

## 19. SSH 模块（未实现/后续规划）

名称：SSH Remote Management  
路径：无当前实现路径；历史相关内容仅在 `初始脚本/logtool.ps1` 和正式 UI 规划文档中。  
职责：未来可能负责远程 Host、连接、命令和终端会话；当前不提供任何桌面端能力。  
入口文件：无。  
依赖：尚未选择协议库，也没有当前可确认的凭据存储方案。  
输入：未来可能包括 Host、Port、User、凭据/密钥、命令和会话选项。  
输出：未来可能包括连接会话、命令输出、退出码和连接错误。  
风险点：

- 凭据、私钥、主机指纹、密码提示、输出编码、超时、取消和审计必须先完成安全设计。
- 不得从旧脚本直接迁移明文凭据或未经验证的命令拼接逻辑。
- SSH 模块应保持与日志分析、报告、SQLite Case 数据的低耦合，不应成为现有 MVP 的隐式依赖。

## 20. 主流程边界

### 日志到报告

```text
监控目录
  → LogFileParser / ArchiveValidator
  → LogInboxService
  → InboxViewModel
  → CaseAnalysisService
  → TaskCenter
  → PluginCatalog + IPluginRunner
  → Case/Task/Report repositories
  → ReportsViewModel
  → ReportsWorkspaceViewModel
  → WebView2 report.html
```

### 报告删除与数据清理

```text
报告中心删除
  → ReportService.DeleteReportAndCaseAsync
  → CaseAnalysisService.DeleteAsync
  → 删除 Cases/<CaseId> 目录
  → 删除 analysis_cases
  → SQLite 外键级联删除 Tasks/Reports/ReportSessions

存储页面清理
  → StorageService.CleanCaseDataAsync
  → 删除 Source 文件和 Extract 目录
  → 保留 Report 和数据库 Case
```

## 21. 维护规则

- 新模块必须在本文件登记入口、依赖、输入、输出和风险。
- 跨层调用必须遵守 `App → Services → Data/Core/PluginSDK` 方向。
- 新增数据库表、配置文件、外部进程或删除语义时，必须同步更新 `.codex/project_context.md`。
- SSH 在完成协议、凭据和会话设计前，只能保持为独立规划项，不能通过占位页面伪装为已实现。
- 文档中的“已实现”必须能在代码入口、测试或运行资产中找到证据。
