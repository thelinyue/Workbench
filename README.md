# 赫菲斯托斯工程工作台（Hephaestus Workbench）

赫菲斯托斯工程工作台（简称“赫工”）是面向内部工程师的 Windows 本地日志分析工作台。

## 当前能力

- 默认监控工作台数据目录下的 `Inbox`，也可以在设置中添加 Downloads、网络盘或其他目录。
- 首页展示最近 5 个有效监控日志，可直接“分析并查看”；也可选择或拖入监控目录外的单个 `.tgz` 日志。
- 识别 `任意前缀_{DeviceID}_{YYYYMMDDHHMM 或 YYMMDDHHMM}.tgz`，前缀不限定为 `diag`。
- 使用现有 `log_analyzer.exe -d <原始日志文件> -o <报告目录>` 执行分析；源文件和解压目录保留在原始日志目录。
- 管理 Analysis Case、后台任务和标准报告。
- 通过分析中心统一搜索日志、案例、任务历史和报告，并使用 WebView2 多标签只读查看；主要结果入口也可直接打开对应解压目录，补充排查报告未覆盖的问题。
- 恢复上次打开的报告、Tab 顺序、当前报告和阅读位置。
- 分项统计日志、解压目录和报告占用，并清理可释放数据。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- WebView2 Runtime

日志分析插件不随源码仓库提供。GitHub Actions 会从官方插件 Releases 仓库获取最新正式版本，将其作为 `PluginSeed` 注入发布目录，首次启动时登记到用户数据目录的 `Plugins/log-analyzer`。

插件开发请参阅 [插件开发文档](docs/plugin-development.md)。v1.1.1 插件中心从独立公开目录 `thelinyue/Hephaestus-Workbench-Plugins` 安装和更新 EXE 插件，并可管理启用状态与默认插件；DLL 插件运行尚未接入。

公开安装包和官方插件位于独立分发仓库 `thelinyue/Hephaestus-Workbench-Releases`。本源码仓库保持私有，发布流程见 [闭源分发说明](docs/distribution.md)。

提供的测试日志文件名为 `宇diag_EC660JJ42230BE31_2608101025.tgz`，可直接放入监控目录；程序会忽略前缀，只校验末尾的设备序列号和时间。

## 构建与运行

```powershell
dotnet restore .\HephaestusWorkbench.sln
dotnet build .\HephaestusWorkbench.sln -c Debug
dotnet run --project .\src\HephaestusWorkbench.App\HephaestusWorkbench.App.csproj
```

发布 Windows x64：

```powershell
dotnet publish .\src\HephaestusWorkbench.App\HephaestusWorkbench.App.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

首次启动会选择 `HephaestusWorkbenchData` 数据目录，并创建：

```text
Database/workbench.db
Cases/<CaseId>/Source
Cases/<CaseId>/Extract
Cases/<CaseId>/Report
Inbox
Plugins
Reports
Logs
Config
Temp
```

首次启动会进入五步配置向导，默认监控 `Inbox`，并写入：

```text
Config/appsettings.json
Config/plugins.json
Config/workspace.json
```

`workspace.json` 中的 `MonitorPaths` 支持多个目录。程序只扫描这些目录中符合命名规则的 `.tgz` 文件。

## 制作 Windows 安装包

安装包使用 .NET 8 self-contained 发布，不需要目标机器预装 .NET Desktop Runtime。默认安装目录为 `C:\Program Files\HephaestusWorkbench`，安装器需要管理员权限；目标机器仍需安装 Microsoft Edge WebView2 Runtime。

直接执行：

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 1.2.1 -PluginBinaryPath '<CI 下载的 log_analyzer.exe>' -RuleEditorBinaryPath '<CI 下载的 rule_editor.exe>'
```

输出位于 `installer\dist`：

```text
HephaestusWorkbench_Setup_v1.2.1.exe
SHA256SUMS.txt
```

同一个 Setup 用于首次安装、覆盖升级和修复；卸载入口由 Windows 设置或控制面板管理。应用数据位于程序安装目录之外，升级和卸载默认保留用户数据。

## 验收流程

推荐在首页完成快速分析：

1. 从“最近日志”中点击“分析并查看”，或把单个 `.tgz` 文件拖入“快速分析日志”区域。
2. 首页会显示检查、等待和分析状态；分析成功后自动切换到应用内报告。
3. 已完成的最近日志可以直接“查看报告”，失败或取消的日志可以重新分析。
4. 首页和分析中心均可快捷打开实际解压目录；若目录已被存储清理，程序会明确提示目录不存在。

首页文件选择和拖放均为原地接管：工作台不会复制日志，插件在原文件旁生成同名解压目录；以后清理或删除案例时会删除原日志及该解压目录。执行删除前，确认框会显示实际绝对路径。

完整日志浏览、异常日志、案例、任务历史和报告统一在“分析中心”管理；当前后台任务可从右上角任务面板查看和取消。默认最多同时打开 10 个报告。

## 说明

初始 PowerShell 脚本只用于确认现有插件的命令行和报告路径约定；SSH、LVM 和脚本菜单中的交互式清理不属于当前 WPF MVP 功能。
