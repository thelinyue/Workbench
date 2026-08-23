# Hephaestus Workbench v2.0.0

Hephaestus Workbench 是面向 Windows 10/11 x64 的本地工程诊断客户端。v2.0.0 将 Shell 固定为四个一级入口：

```text
工作
├─ 分析中心
└─ SSH 终端

扩展
└─ 扩展中心

系统
└─ 设置
```

## v2 核心行为

- 默认启动进入分析中心。
- 分析中心固定使用日志分析扩展，不选择分析引擎，只选择“综合分析”或后续支持的“存储分析”。
- 单日志快速分析成功后使用默认电脑浏览器打开 `Report/index.html`；批量与目录监控不会自动打开多个标签。
- Workspace 扩展只能从扩展中心进入固定受控 Host，不能贡献侧栏导航。
- 报告不再使用内嵌 WebView2；WebView2 仅保留给 SSH 终端和 Workspace Extension。

## 开发环境

- Windows 10/11 x64
- .NET 8 SDK
- WebView2 Runtime

```powershell
dotnet restore .\HephaestusWorkbench.sln --configfile .\NuGet.config
dotnet build .\HephaestusWorkbench.sln -c Release --no-restore
dotnet test .\HephaestusWorkbench.sln -c Release --no-build --no-restore
```

普通源码构建不携带正式扩展资产，也不提供 unsigned/developer extension mode。

## 数据目录

新工作区使用 schema v2：

```text
Config/
  workspace.json
  appsettings.json
  extensions.json
Database/
  workbench.db
Extensions/
Operations/
Logs/
Temp/
Cache/
```

v2.0.0 不兼容旧数据库、旧配置、旧 `Plugins`、旧 manifest/catalog、旧 `report.html` 或旧客户端。发现旧工作区时只显示绝对路径和“打开目录 / 退出”，不会迁移、备份或删除旧数据。

## 正式安装包

正式构建固定读取仓库内：

```text
distribution/bundled-extensions.json
```

清单必须锁定真实签名扩展 ZIP 的 URL、size、SHA-256、Ed25519 keyId/signature。构建脚本不会查询远端最新版本，也不会提取单个插件 EXE。

```powershell
.\installer\build-installer.ps1 -Configuration Release -Version 2.0.0
```

只有真实签名 Log Analyzer、锁定清单、宿主正式公钥信任锚和 Windows 10/11 全新安装烟测全部就绪后，才能发布 v2.0.0。

扩展开发参阅 `docs/plugin-development.md`，发布边界参阅 `docs/distribution.md`。
