# Hephaestus Workbench 项目上下文

> 文档版本：v1.0  
> 基线日期：2026-08-11  
> 维护规则：任何代码修改任务开始前，先阅读本文档和 `docs/module_map.md`。代码现状优先于本文档中的推断、需求文档和未来规划。

## 项目名称

Hephaestus Workbench（赫菲斯托斯工程工作台，简称“赫工”）。

## 项目目标

这是一个面向 Windows 工程师的本地桌面工程工作台，目标是统一日志分析、案例管理、报告查看、插件运行、数据清理和工作区配置等能力，降低从“拿到日志”到“完成分析并查看报告”的操作成本。

当前产品定位是单机、本地文件系统和本地 SQLite 数据库驱动的日志分析工作台，不是客户管理系统、工单系统、多人协作平台或权限管理平台。

## 当前实现边界

### 已实现能力

- WPF 桌面 Shell、顶部状态区、左侧导航和页面级 ViewModel。
- 日志收件箱：监控一个或多个目录，识别 `.tgz` 文件名，解析设备 ID 和日志时间，并校验 gzip/tar 内容。
- Analysis Case 和后台分析 Task 的创建、状态更新、取消、删除和列表展示。
- 通过外部 `log_analyzer.exe` 进程执行日志分析，并将标准化报告接入报告中心。
- 插件清单扫描、内置插件复制、旧版 runner 兼容和标准 EXE runner。
- 报告查询、筛选、WebView2 只读查看、多 Tab、滚动位置保存和启动恢复。
- SQLite 业务数据、JSON 配置、案例文件目录和存储统计/清理。
- 首次运行向导、数据目录初始化、数据库幂等初始化、配置迁移和 Windows 安装器。

### 未实现或不属于当前 WPF MVP 的能力

- SSH 远程管理没有桌面端页面、服务、协议库、凭据模型或通信实现。
- `HephaestusWorkbench.PluginSDK` 定义了 DLL 插件接口，但当前生产运行链路没有 DLL 加载器。
- `初始脚本/logtool.ps1` 中的 SSH 命令生成、LVM 配置处理和交互式清理仍属于遗留 PowerShell 工具，不等同于 WPF 应用模块。

## 技术栈

| 层次 | 当前技术 | 证据与边界 |
| --- | --- | --- |
| 前端/表现层 | C#、WPF、XAML、MVVM 风格 ViewModel | `src/HephaestusWorkbench.App`；页面通过 `DataTemplate` 绑定 ViewModel |
| 桌面框架 | .NET 8 WPF；安装器使用 .NET 8 Windows Forms | App 项目启用 `UseWPF` 和 `UseWindowsForms`；Setup 项目为 WinForms |
| 后端/业务层 | .NET 8 进程内 Services | 没有 ASP.NET、HTTP API、服务端进程或远程后端；业务由 `HephaestusWorkbench.Services` 承载 |
| 领域层 | 领域模型和仓储接口 | `HephaestusWorkbench.Core` 不依赖 Data、Services 或 UI |
| 数据层 | SQLite + Microsoft.Data.Sqlite 8.0.8 | 数据库文件为 `Database/workbench.db`，仓储实现位于 Data 项目 |
| 配置层 | JSON 文件 + SQLite 旧版键值兼容 | `appsettings.json`、`plugins.json`、`workspace.json`；SQLite `app_settings` 用于兼容迁移/镜像 |
| 文件监控 | `FileSystemWatcher` | `LogInboxService` 监控配置目录中的 `.tgz` 文件 |
| 报告查看 | Microsoft.Web.WebView2 1.0.2903.40 | 本地 `report.html` 只读加载，WebMessage 仅用于保存滚动位置 |
| 插件通信 | 外部 EXE 进程 + 命令行参数 + 文件系统输出 | `ProcessStartInfo` 启动插件；标准协议使用 `--case/--input/--output` |
| UI MVVM 支持 | CommunityToolkit.Mvvm 8.3.2 已声明 | 当前 ViewModel 主要使用项目内 `ViewModelBase` 和 `DelegateCommand` |
| 构建 | `dotnet` CLI、MSBuild、PowerShell 安装脚本 | `global.json` 锁定 .NET SDK 8.0.100，允许最新 feature roll-forward |
| 测试 | xUnit 2.9.2、Microsoft.NET.Test.Sdk 17.11.1 | `tests/HephaestusWorkbench.Tests` |

## 项目目录结构

```text
Hephaestus Workbench/
├── .codex/
│   └── project_context.md                 # 本文档
├── docs/
│   └── module_map.md                      # 模块地图
├── src/
│   ├── HephaestusWorkbench.App/           # WPF 桌面应用、页面、ViewModel、主题
│   ├── HephaestusWorkbench.Core/          # 领域模型、状态和仓储接口
│   ├── HephaestusWorkbench.Data/          # SQLite 连接、建库、迁移和仓储实现
│   ├── HephaestusWorkbench.PluginSDK/     # 插件 manifest、执行上下文和运行契约
│   └── HephaestusWorkbench.Services/      # 初始化、收件箱、分析、插件、报告、配置和存储业务
├── tests/
│   └── HephaestusWorkbench.Tests/         # 单元测试和集成式 SQLite/文件系统测试
├── installer/
│   ├── HephaestusWorkbench.Setup/         # Windows Forms 安装器/升级/卸载入口
│   ├── build-installer.ps1                # 发布应用、打包 Payload 和生成安装器
│   └── dist/                              # 当前工作区已有的安装器输出资产
├── 插件/                                  # 内置 log_analyzer.exe 和测试日志资产
├── 初始脚本/                              # 旧版 PowerShell logtool 工具
├── HephaestusWorkbench_Formal_Document_Package_v1.0/ # SRS/SDS/TDD/UI 等设计资料
├── HephaestusWorkbench.sln                # .NET 解决方案入口
├── Directory.Build.props                   # 全局 C#、版本和产品元数据
├── global.json                             # .NET SDK 版本策略
├── NuGet.config                            # NuGet 源配置
└── README.md                               # 构建、运行、验收和安装说明
```

### 核心文件

| 文件 | 作用 |
| --- | --- |
| `src/HephaestusWorkbench.App/App.xaml.cs` | 应用启动、首次运行向导、组合根 `WorkbenchHost` 和生命周期释放 |
| `src/HephaestusWorkbench.App/ViewModels/MainViewModel.cs` | 顶层导航、全局状态和页面 ViewModel 组装 |
| `src/HephaestusWorkbench.Services/CaseAnalysisService.cs` | 串联 Case、Task、插件 runner、文件目录和报告记录的核心业务流程 |
| `src/HephaestusWorkbench.Services/LogInboxService.cs` | 目录监控、文件扫描、压缩包校验和收件箱事件 |
| `src/HephaestusWorkbench.Services/PluginCatalog.cs` | 插件 manifest 扫描、入口校验和问题汇总 |
| `src/HephaestusWorkbench.Services/ReportService.cs` | 报告查询、报告会话持久化和删除语义 |
| `src/HephaestusWorkbench.Data/DatabaseInitializer.cs` | SQLite 表、索引和兼容字段迁移 |
| `src/HephaestusWorkbench.Data/DataPaths.cs` | 程序数据目录、数据库、Case 和配置路径约定 |
| `src/HephaestusWorkbench.PluginSDK/PluginContracts.cs` | 插件类型、清单、执行上下文和 runner 契约 |
| `installer/build-installer.ps1` | Windows x64 self-contained 应用和安装包构建流程 |
| `tests/HephaestusWorkbench.Tests` | 配置、解析、仓储、插件、安装器和报告工作区验证 |

## 整体架构

### 分层结构

```text
┌─────────────────────────────────────────────────────────────┐
│ App/UI                                                      │
│ WPF Window / XAML Views / ViewModels / WebView2             │
└───────────────────────────┬─────────────────────────────────┘
                            │ commands, properties, events
┌───────────────────────────▼─────────────────────────────────┐
│ Services                                                    │
│ Inbox / Case Analysis / Task Center / Plugin / Report       │
│ Storage / Settings / Configuration / Initialization / Log    │
└───────────────┬──────────────────────────┬──────────────────┘
                │ repository interfaces   │ plugin contracts
┌───────────────▼──────────────┐   ┌───────▼─────────────────┐
│ Data + Core                  │   │ PluginSDK + Plugin EXE │
│ SQLite / models / paths      │   │ process + files         │
└───────────────┬──────────────┘   └─────────────────────────┘
                │
┌───────────────▼─────────────────────────────────────────────┐
│ Local persistence                                            │
│ workbench.db / JSON config / Case files / Reports / Logs     │
└─────────────────────────────────────────────────────────────┘
```

### 依赖方向

- `App` 依赖 `Core`、`Data`、`Services` 和 `PluginSDK`。
- `Services` 依赖 `Core`、`Data` 和 `PluginSDK`，负责业务编排，不应由 ViewModel 直接拼装数据库 SQL 或插件进程参数。
- `Data` 依赖 `Core`，只实现仓储、SQLite 连接、路径和数据库初始化。
- `PluginSDK` 依赖 `Core`，提供稳定的插件边界，不依赖 WPF 或具体 runner。
- `Core` 不依赖 UI、Services、Data 或具体第三方基础设施。
- `Installer` 是独立的部署工具，不属于运行时业务链路。
- `初始脚本`和`插件`是辅助/遗留资产，不应被当作 WPF 服务层依赖。

### 通信层

当前系统没有网络 API。实际通信方式只有以下几类：

1. UI 与业务：ViewModel 调用 Services，Services 通过异步方法返回结果或发布事件。
2. 目录变化：`FileSystemWatcher` 触发收件箱刷新。
3. 数据库：Data 项目通过 `Microsoft.Data.Sqlite` 访问本地 SQLite。
4. 配置：Configuration/Settings Service 读写 JSON，并通过原子替换保证文件不会留下半份内容。
5. 插件：Services 启动外部 EXE，使用命令行参数传入 Case、输入和输出目录，使用退出码、stderr 和报告文件判断结果。
6. 报告页面：WebView2 加载本地 HTML；页面只通过 WebMessage 回传滚动位置。

## 关键数据流

### 首次启动与初始化

```text
App.OnStartup
  → WorkbenchHost.CreateAsync
  → 读取 LocalApplicationData/HephaestusWorkbench/bootstrap.json
  → 无有效数据库时打开 FirstRunWizard
  → WorkbenchInitializationService
      → 创建数据目录
      → DatabaseInitializer 创建/迁移 SQLite
      → 写入 workspace.json/appsettings.json/plugins.json
      → 复制并登记内置插件
  → WorkbenchHost.InitializeAsync
      → 初始化数据库和配置
      → 启动 LogInboxService
      → 创建 MainViewModel
      → 打开主窗口
```

### 日志分析主流程

```text
用户将 .tgz 放入监控目录
  → FileSystemWatcher / RefreshAsync
  → LogFileParser 解析文件名
  → ArchiveValidator 校验 gzip/tar
  → InboxViewModel 展示 LogInboxItem
  → 用户点击开始分析
  → CaseAnalysisService.StartAsync
      → 创建 Cases/<CaseId>/Source、Extract、Report
      → 复制原始日志
      → 写入 analysis_cases 和 analysis_tasks
      → TaskCenter 排队并限制并行数
      → LegacyLogAnalyzerRunner 或 StandardExePluginRunner
      → 生成 report.html
      → 更新 Case/Task 状态
      → 写入 reports
      → UI 通过 StateChanged 刷新
```

### 报告生命周期

```text
reports + analysis_cases + plugin_info
  → SqliteReportRepository.ListAsync(ReportQuery)
  → ReportsViewModel 搜索/筛选
  → ReportsWorkspaceViewModel.OpenReportAsync
  → ReportTabViewModel
  → ReportViewerControl + WebView2 加载 report.html
  → 滚动位置节流保存为 ReportSession
  → 应用退出/延迟保存
  → 下次启动按顺序和活动 Tab 恢复
```

### 配置、存储和清理

- `SettingsViewModel` 通过 `SettingsService` 修改监控目录、主题、最大报告 Tab 数和恢复开关。
- `SettingsService` 优先使用 JSON 配置，SQLite `app_settings` 保留旧版本兼容读取/镜像。
- `StorageService` 统计数据根目录和 Case 下的日志、解压目录、报告占用。
- 清理 Case 数据时删除原始日志和 Extract，保留报告；删除 Case/报告时由 `CaseAnalysisService.DeleteAsync` 删除 Case 目录并依赖 SQLite 外键删除关联记录。

## 核心模块说明

| 模块 | 主要入口 | 责任边界 |
| --- | --- | --- |
| App/UI | `App.xaml.cs`、`MainViewModel` | 启动、导航、用户交互、页面状态和 WebView2 生命周期 |
| Core | Models、`IRepositories.cs` | 定义业务对象、状态和持久化抽象，不执行 IO |
| Data | `DataPaths`、`DatabaseInitializer`、SQLite repositories | 管理本地目录、SQLite schema、查询和持久化 |
| Log Inbox | `LogInboxService` | 监控目录、识别文件、校验压缩包、维护内存收件箱 |
| Case Analysis | `CaseAnalysisService` | 创建 Case/Task，调用插件，更新状态并登记 Report |
| Task Center | `TaskCenter` | 任务排队、最多两个并发槽位和取消令牌 |
| Plugin | `PluginCatalog`、`PluginProvisioningService`、两类 runner | 清单扫描、内置插件安装、外部 EXE 执行和报告输出校验 |
| Report | `ReportService`、`ReportsWorkspaceViewModel` | 报告查询、Tab、会话恢复、WebView2 查看和删除语义 |
| Storage | `StorageService` | 磁盘占用统计和按 Case 清理原始数据 |
| Configuration | `WorkbenchConfigurationService`、`SettingsService` | JSON 配置、默认值、原子写入和旧配置迁移 |
| Initialization | `WorkbenchInitializationService` | 首次运行目录、数据库、配置和内置插件初始化 |
| Installer | `HephaestusWorkbench.Setup`、`build-installer.ps1` | self-contained 发布、安装、升级、卸载和数据备份 |
| SSH | 无 | 当前未实现，仅保留为未来规划能力 |

## 数据结构说明

### 领域模型和状态

- `AnalysisCase`：Case ID、展示名、原始文件名、设备 ID、日志时间、状态、Source/Extract/Report 路径和错误信息。
- `AnalysisTask`：Task ID、Case ID、插件 ID、状态、起止时间、报告路径和错误信息。
- `Report`：报告 ID、Case ID、报告目录、插件 ID 和生成时间。
- `ReportSummary`：报告中心使用的 Case、设备、插件、路径、可用性聚合信息。
- `ReportSession`：报告 Tab 顺序、活动状态、滚动位置和最近打开时间。
- `PluginInfo`：数据库缓存的插件发现信息。
- `LogInboxItem`：不落库的收件箱文件信息和校验结果。

状态枚举：

- Case：`Created`、`Ready`、`Running`、`Completed`、`Failed`。
- Task：`Waiting`、`Running`、`Completed`、`Failed`、`Cancelled`。

### SQLite 表

| 表 | 用途 | 关键关系 |
| --- | --- | --- |
| `analysis_cases` | 保存 Case 生命周期和文件路径 | 主表 |
| `analysis_tasks` | 保存插件分析任务 | `case_id → analysis_cases.id`，级联删除 |
| `plugin_info` | 缓存插件名称、版本、类型、入口和启用状态 | 报告查询用于显示插件名称 |
| `reports` | 保存标准化报告目录 | `case_id → analysis_cases.id`，级联删除 |
| `report_sessions` | 保存打开的报告 Tab | `report_id → reports.id`，唯一约束 |
| `app_settings` | 旧版键值设置兼容存储 | `key` 主键 |

数据库由 `DatabaseInitializer` 使用幂等 DDL 创建，并对旧版 `reports.plugin_id` 执行兼容迁移和回填。

### JSON 配置

| 文件 | 内容 |
| --- | --- |
| `Config/workspace.json` | `DataPath`、`MonitorPaths` |
| `Config/appsettings.json` | `Theme`、`MaxReportTabs`、`AutoRestoreReports` |
| `Config/plugins.json` | 插件 ID、版本和启用状态 |
| 用户级 bootstrap | `%LocalAppData%/HephaestusWorkbench/bootstrap.json`，指向用户数据根目录 |

### 文件系统目录

```text
HephaestusWorkbenchData/
├── Database/workbench.db
├── Cases/<CaseId>/Source                  # 原始日志
├── Cases/<CaseId>/Extract                 # 插件/旧 runner 解压内容
├── Cases/<CaseId>/Report                  # 标准报告目录，通常含 report.html
├── Inbox                                 # 默认监控目录
├── Plugins/<PluginId>/                    # manifest.json 和插件入口
├── Reports                                # 预留的报告根目录，目前主要由 DataPaths 创建
├── Logs/workbench.log
├── Config/
└── Temp/
```

### 插件 manifest

当前清单字段：`id`、`name`、`version`、`type`、`entry`，可选 `runner` 和 `reportPath`。内置插件使用：

```json
{
  "id": "log-analyzer",
  "name": "日志分析插件",
  "version": "1.49",
  "type": "Exe",
  "entry": "log_analyzer.exe",
  "runner": "legacy-log-analyzer",
  "reportPath": "report/report.html"
}
```

标准 EXE runner 约定：

```text
Plugin.exe --case <CaseId> --input <SourcePath> --output <OutputPath>
退出码 0 + output/report.html 存在 = 成功
非 0 或 report.html 缺失 = 失败
```

## 开发规范

- 关键类和关键设计必须补充清晰的中文注释，说明职责、边界、生命周期和扩展方式。
- 面向部署者的错误和日志使用明确中文信息；日志应说明发生了什么以及用户可采取的动作。
- 编码前先确认事实和边界；遇到无法从仓库推断的高影响决策，先说明假设和权衡。
- 优先使用最少代码解决明确需求，不为未来场景提前引入无需求的抽象、配置项或错误分支。
- 精准修改：只修改与需求直接相关的文件，不顺手重构相邻代码或删除预存的无关死代码。
- 业务 IO 使用异步 API，避免在 WPF UI 线程同步等待数据库、文件或插件进程。
- UI 通过 ViewModel 和 Services 操作业务；不要在 XAML code-behind 或页面中直接写 SQL、修改 Case 目录或启动插件。
- 数据库访问集中在 Data 仓储；schema 变更必须有幂等迁移和测试。
- 插件执行必须通过 `PluginSDK` 的上下文和 runner 边界，不能在页面中拼接命令行。
- 修改完成后按风险执行构建、测试和真实场景验证，并如实记录未验证部分。
- 文档、代码、测试和日志中的名称应与真实路径、表名、配置键和入口文件一致。

## 修改代码注意事项

1. 修改启动顺序时，保持 `WorkbenchHost` 作为组合根，避免把基础设施组装逻辑散落到页面。
2. 修改数据目录时，同时检查 `DataPaths`、首次运行向导、bootstrap 指针、安装器和 README 约定。
3. 修改数据库模型时，同时更新 Core 模型、Repository 接口、SQLite schema、读写映射、迁移和测试。
4. 修改插件协议或 runner 时，必须保持 legacy runner 与标准 EXE runner 的兼容边界，并验证取消、非零退出码和报告缺失。
5. 修改报告查看器时，注意 WebView2 控件生命周期、Tab 复用、滚动位置保存和报告文件缺失场景。
6. 修改删除和清理语义时，明确区分“删除原始日志/Extract”和“删除 Case、报告及数据库记录”；二者都属于不可逆操作。
7. 修改配置时，保持 JSON 原子写入、路径绝对化、默认 Inbox 和旧版 SQLite 键兼容。
8. 修改异步事件或 `FileSystemWatcher` 时，关注 UI 线程切换、重复刷新、取消、异常观察和资源释放。
9. 未来增加 SSH 时，必须先独立设计凭据安全、主机指纹、会话生命周期、命令取消、输出编码和审计边界，再进入 App 导航。

## 禁止事项

- 不把 SSH 页面、假的 SSH 服务或占位按钮当作已实现功能。
- 不在未设计凭据安全前保存明文 SSH 密码或私钥。
- 不在 App/UI 直接访问 SQLite、修改配置文件或启动外部进程。
- 不让插件输出写入程序安装目录；用户数据和程序目录必须保持隔离。
- 不通过删除数据库、覆盖配置或清空用户目录解决迁移/初始化问题。
- 不未经确认改变报告删除、Case 清理、插件执行和数据保留语义。
- 不把遗留 PowerShell 脚本中的能力默认视为 WPF MVP 能力。
- 不引入未被需求要求的网络后端、多用户、权限系统或远程共享数据库。
- 不以“构建通过”代替运行时、插件、WebView2、目录监控和安装升级验证。

## 当前已知问题和风险

以下条目是当前代码或正式设计资料能够直接支持的观察，不代表本次任务要修复它们：

1. **SSH 未实现**：应用没有 SSH 项目、协议库、页面或服务；只有 `初始脚本/logtool.ps1` 的 SSH 命令生成和文档中的后续规划。
2. **DLL 插件未接入**：SDK 定义 `IAnalysisPlugin`，但生产运行时只有 EXE runner；不能宣称支持 DLL 插件热加载。
3. **插件启用状态链路不完整**：`plugins.json` 和 `PluginConfigEntry.Enabled` 存在，但案例分析选择插件时主要依据扫描结果和 legacy runner，未形成完整的启用/禁用策略。
4. **插件数据库登记存在断点**：`plugin_info` 表和 `SqlitePluginInfoRepository` 存在，报告查询会 JOIN 该表；启动流程主要写入 `plugins.json`，生产链路没有完整使用 `PluginsRepository` 登记发现结果，因此插件名称可能回退为插件 ID 或“未知插件”。
5. **报告根目录约定不一致**：`DataPaths` 创建 `Reports` 目录，但 Case 分析实际使用 `Cases/<CaseId>/Report`；后续修改必须先确定是否保留预留目录以及报告的唯一事实来源。
6. **任务恢复能力有限**：TaskCenter 的取消令牌和队列仅存在于当前进程；应用重启后，数据库中的 Waiting/Running 任务没有明确的恢复、重试或孤儿状态修复流程。
7. **后台异常边界需要加强**：分析任务通过 fire-and-forget 方式入队；runner 之外的数据库或文件异常需要确保被观察、记录并转换为可理解的 Task/Case 失败状态。
8. **插件安全边界需要明确**：manifest 入口、外部 EXE、插件输出 HTML 和本地 WebView2 都属于执行/内容信任边界，当前未体现签名、沙箱或路径越界防护。
9. **内置插件更新判断较弱**：Provisioning 主要依据 EXE 文件大小判断是否复制；同大小不同内容的插件更新可能不会被识别。
10. **收件箱扫描的规模风险**：启动和刷新会按目录顺序校验压缩包；大量文件、网络盘或不稳定文件系统可能延长启动/刷新时间。
11. **遗留脚本边界**：`初始脚本`仍包含日志解压、SSH 命令生成、LVM 处理和永久删除逻辑，与 WPF MVP 的数据和安全语义不同，不应直接复用而不重新设计。
12. **仓库基线卫生**：当前仓库尚无 Git commit，且存在安装器输出、插件二进制和测试资产；后续发布前应建立清晰的源码、生成物和大文件管理策略。

## 架构评估报告

### 1. 当前项目健康度

**中等偏上，适合作为可运行 MVP 继续演进。**

- 解决方案分层清晰，构建无警告。
- 当前测试基线为 31 个测试全部通过。
- 日志收件箱、Case、Task、插件、报告、配置和存储主流程已经闭环。
- 主要健康度扣分来自任务恢复、插件状态登记、DLL/SSH 未实现和外部进程安全边界，而不是当前基础编译质量。

### 2. 架构优点

- Core 通过仓储接口隔离领域模型与 SQLite 实现。
- Services 集中业务编排，UI 没有直接承担数据库和插件进程细节。
- 用户数据目录与程序安装目录分离，降低升级覆盖业务数据的风险。
- 配置原子写入、数据库幂等初始化和旧版迁移意识较完整。
- PluginSDK、legacy runner 和标准 runner 为插件兼容提供了明确入口。
- Report Workspace 对 Tab、恢复、滚动位置和 WebView2 生命周期有独立模型。

### 3. 潜在风险

- 外部插件是本地进程执行边界，当前缺少更强的信任和路径安全策略。
- 进程重启后后台任务状态没有完整恢复协议。
- JSON 配置、SQLite 旧键和 plugin_info 的多来源状态可能产生不一致。
- 报告目录存在预留路径与实际路径并存的问题。
- 旧脚本能力与新桌面端能力容易被误认为同一产品模块。

### 4. 建议优化方向

1. 先补齐任务状态机、重启恢复、异常收敛和可观测性，再扩大插件能力。
2. 建立单一插件注册来源，明确 `plugins.json`、`plugin_info` 和 Catalog 的关系，并真正执行启用状态。
3. 统一报告目录语义，补充报告文件清单、版本和兼容迁移策略。
4. 为插件 manifest、入口路径、输出目录和报告 HTML 增加信任边界验证。
5. 在新增 SSH 前完成独立安全设计和最小端到端场景，不从旧脚本直接搬运凭据或命令逻辑。
6. 对大目录、网络盘、损坏压缩包、WebView2 缺失和安装升级进行真实 Windows 验收。

### 5. 后续开发建议

- P0：保持日志 → Case → Task → Report 主链路稳定，补充失败恢复和任务状态反馈。
- P1：统一空状态、错误信息、插件健康项、存储清理预览和报告 Tab 交互。
- 后续：独立设计 SSH 模块，明确协议库、凭据、主机指纹、编码、会话和审计边界后再进入架构。
- 发布前：建立 Git 提交基线，明确二进制/大文件策略，验证 self-contained 安装器和 WebView2 前置条件。
