# 赫菲斯托斯工程工作台（Hephaestus Workbench）

赫菲斯托斯工程工作台（简称“赫工”）是面向内部工程师的 Windows 本地日志分析工作台。

## 当前能力

- 默认监控工作台数据目录下的 `Inbox`，也可以在设置中添加 Downloads、网络盘或其他目录。
- 识别 `任意前缀_{DeviceID}_{YYYYMMDDHHMM 或 YYMMDDHHMM}.tgz`，前缀不限定为 `diag`。
- 使用现有 `log_analyzer.exe -d <日志文件>` 执行分析。
- 管理 Analysis Case、后台任务和标准报告。
- 通过独立报告中心搜索历史报告，并使用 WebView2 多标签只读查看。
- 恢复上次打开的报告、Tab 顺序、当前报告和阅读位置。
- 分项统计日志、解压目录和报告占用，并清理可释放数据。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- WebView2 Runtime

当前工作区中的插件位于 `插件/log_analyzer.exe`。构建时会将它作为 `PluginSeed` 复制到发布目录，首次启动时登记到用户数据目录的 `Plugins/log-analyzer`。

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

```

输出位于 `installer\dist`：

```text
HephaestusWorkbench_Setup.exe
HephaestusWorkbench_Update.exe
HephaestusWorkbench_Uninstall.exe
```

如需离线自动安装 WebView2，请将官方 x64 安装程序放到：

```text
installer\dependencies\MicrosoftEdgeWebView2RuntimeInstallerX64.exe
```

缺少该文件时，安装器会在检测失败时给出中文提示，不会伪造运行环境已满足。

## 验收流程

将符合命名规则的 `.tgz` 文件放入监控目录，在日志收件箱中点击“开始分析”，然后在案例或任务页面查看状态。分析成功后，可从案例或独立报告中心打开只读报告；默认最多同时打开 10 个报告。

## 说明

初始 PowerShell 脚本只用于确认现有插件的命令行和报告路径约定；SSH、LVM 和脚本菜单中的交互式清理不属于当前 WPF MVP 功能。
