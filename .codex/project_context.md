# Hephaestus Workbench 项目上下文

> 文档状态：v2.0.0 实施中
> 状态日期：2026-08-23
> 代码现状优先于未来计划；开始修改前阅读本文件和 `docs/module_map.md`。

## 产品定位

Hephaestus Workbench 是面向 Windows 工程师的轻量工程诊断宿主。一级入口固定为分析中心、SSH 终端、扩展中心和设置，默认进入分析中心。扩展不能贡献侧栏导航。

v2.0.0 是全新正式工作区，不兼容旧数据库、旧配置、旧扩展包或旧客户端。检测到旧工作区时只显示绝对路径并允许打开目录或退出；不迁移、不备份、不删除旧数据。

## 当前已经落地

- schema v2 bootstrap、workspace、appsettings 和 extensions 配置。
- 旧工作区启动门禁和同盘 staging 首次初始化。
- 固定 Shell 与精简分析中心。
- 综合分析/存储分析范围选择；分析引擎由唯一可用日志分析扩展决定。
- manifest/catalog v2 DTO、验证、Catalog 下载、Ed25519 包校验、版本目录、健康状态、回滚和版本租约。
- Analysis Process v1 宿主，报告固定生成到 `Report/index.html`。
- 报告路径安全校验和 Windows 默认浏览器打开。
- 扩展中心的发现、已安装、更新、类型筛选、身份冲突和兼容性状态。
- 固定 Workspace Host、WebView2 隔离策略、同源消息校验和无 Bridge 权限默认值。
- 删除旧 Marketplace、Provisioning、Runner、第三方 DLL/WPF 扩展入口和嵌入式报告界面。

## 当前尚未实现

- SSH.NET 终端服务、命令执行服务、凭据、Host Key、xterm.js 和重连。
- Maintenance Planner/Policy/Executor、只读发现、不可变计划和操作仓储。
- Analysis Content 目前只有 manifest 组合校验，没有内容 DTO/schema 或宿主应用流程；Maintenance Content 已有预留 DTO，但没有加载与执行流程。
- 正式发布信任锚、至少一个真实签名日志分析包、`BundledExtensions/` 和锁定清单。
- 安装器/Release Workflow v2 闭环和 Windows 10/11 手工烟测。

## 技术与分层

| 层 | 职责 |
| --- | --- |
| Core | 领域模型、仓储契约、可序列化请求和结果 |
| Data | SQLite schema v2、仓储实现、DataPaths |
| Services | 文件/浏览器/进程能力、Analysis Host、Extension Host、初始化和设置 |
| App | WPF Shell、Feature ViewModel/Page、WorkspaceHostWindow |
| PluginSDK | manifest/catalog/process/bridge/workflow JSON DTO 与验证器 |

跨模块接口不得暴露 WPF 控件、SQLite Connection、WebView2 对象、SSH.NET 类型或具体文件实现。

## 数据与配置

工作区数据根包含：

```text
Database/workbench.db
Config/workspace.json
Config/appsettings.json
Config/extensions.json
Extensions/<id>/<version>/
Cases/
Inbox/
Rules/
Logs/
Temp/
Cache/
```

当前分析数据库链路：

```text
analysis_cases
analysis_tasks
reports
```

扩展 Registry 以 `Extensions/<id>/current.json` 为事实来源；`extensions.json` 只保存启用状态、更新通道和默认分析 capability。配置中不得保存密码、私钥口令或凭据密文。

## 分析与报告边界

```text
LogInboxItem
→ ExtensionSettingsStore
→ ExtensionRegistry 版本租约
→ CaseAnalysisService
→ TaskCenter
→ AnalysisProcessHost
→ Report/index.html
→ ReportOpenService
→ 默认浏览器
```

快速单日志成功后可自动打开报告；批量/监控分析不自动打开多个浏览器标签。App 不承载报告 Tab。

## 扩展安全边界

- 只接受 manifest/catalog schema v2。
- 不支持第三方 DLL、WPF View 或 ViewModel。
- 包安装必须通过大小、SHA-256、Ed25519、发布者信任、路径和 Host API 校验。
- Catalog 公钥不能自行获得信任。
- 相同 ID/版本不同内容拒绝安装。
- Workspace Host 默认禁止网络、任意文件、Shell、进程、下载和未授权 Bridge 方法。
- 运行任务持有具体版本租约；新版本只影响新任务。

## 开发规范

1. 先写失败测试，再做最小实现。
2. 关键类写中文设计注释。
3. 用户可见错误和日志使用明确中文。
4. 只修改任务需要的文件，不做相邻重构。
5. 不新增兼容层；v2 正式版不迁移旧数据。
6. 正式签名私钥只来自 CI Secrets，不进入仓库。
7. 每阶段执行 Restore、Release Build、全量 Test 和 `git diff --check` 后再提交。

## 当前验证基线

以当前分支最新一次实际命令输出为准，不在本文硬编码测试数量。标准命令：

```powershell
dotnet restore .\HephaestusWorkbench.sln --configfile .\NuGet.config
dotnet build .\HephaestusWorkbench.sln -c Release --no-restore
dotnet test .\HephaestusWorkbench.sln -c Release --no-build --no-restore
git diff --check
```
