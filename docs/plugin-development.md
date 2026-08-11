# Hephaestus Workbench 插件开发文档

本文说明如何为赫菲斯托斯工程工作台开发和登记本地日志分析插件。

## 1. 当前支持范围

v1.1.0 同时支持本地插件目录和官方在线目录：

- 支持扫描用户数据目录下的 `Plugins` 子目录中的 `manifest.json`。
- 支持 EXE 标准插件。
- 兼容现有的 `log_analyzer.exe` 旧版插件协议。
- 支持在插件中心刷新、查看扫描问题、打开插件目录和打开本开发文档。
- 官方 EXE 插件可通过在线目录安装和升级；安装包必须通过 HTTPS、SHA-256、大小、路径边界和清单一致性校验。
- 暂不提供数字签名、沙箱隔离、社区投稿或 DLL 插件执行。
- `IAnalysisPlugin` 和 `PluginType.Dll` 目前只是 SDK 契约，尚未接入 DLL 加载运行器；DLL 插件不能作为当前版本的可执行插件发布。

插件会以当前 Windows 用户权限启动。只应安装来源可信、经过测试的插件。

## 2. 插件目录结构

工作台数据目录默认由首次运行向导选择，插件目录为：

```text
<工作台数据目录>/Plugins/
└── sample-analyzer/
    ├── manifest.json
    └── sample-analyzer.exe
```

推荐每个插件使用独立目录。插件中心会递归查找 `manifest.json`，但入口文件必须位于清单所在目录或其子路径内，并且入口文件必须真实存在。

手工插件开发完成后，将整个插件目录复制到 `Plugins` 目录，打开工作台的“插件中心”，点击“刷新”即可登记。手工插件不会被在线市场覆盖或卸载。扫描失败时，页面会显示中文错误并写入工作台 `Logs/workbench.log`。

官方在线目录固定由公开分发仓库维护。`schemaVersion` 当前为 `1`，每个条目必须包含 `id`、`name`、`description`、`version`、`type`、`packageUrl`、`sha256`、`packageSize`、`minimumAppVersion` 和 `releaseNotesUrl`。插件 ZIP 根目录必须直接包含 `manifest.json` 和入口文件，目录字段必须与本地 manifest 的 ID、版本和类型一致。

## 3. manifest.json

最小的 EXE 标准插件清单如下：

```json
{
  "id": "sample-analyzer",
  "name": "示例日志分析插件",
  "version": "1.0.0",
  "type": "Exe",
  "entry": "sample-analyzer.exe"
}
```

字段说明：

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 稳定且唯一的插件 ID。发布后不要随意修改，否则历史任务和报告无法按原 ID 关联。 |
| `name` | 是 | 插件中心和报告列表中显示的名称。 |
| `version` | 是 | 插件版本字符串，由插件作者维护。 |
| `type` | 是 | 当前可执行插件填写 `Exe`。`Dll` 目前不会被运行。 |
| `entry` | 是 | 相对于 `manifest.json` 所在目录的入口文件路径，例如 `bin/analyzer.exe`。不要填写绝对路径。 |
| `runner` | 否 | 仅现有旧版插件使用 `legacy-log-analyzer`。标准 EXE 插件不要填写。 |
| `reportPath` | 否 | 旧版清单兼容字段。标准 EXE 当前固定检查输出目录中的 `report.html`。 |

### ID 和路径约定

- `id` 建议使用小写字母、数字和短横线，例如 `sample-analyzer`。
- 不要让不同插件使用相同的 `id`。
- `entry` 必须使用相对路径，不能通过 `..` 指向插件目录之外。
- 插件附带的 CSS、JavaScript、图片等报告资源，应与 `report.html` 一起放在输出目录中，并使用相对路径引用。

## 4. EXE 标准运行协议

工作台会在插件目录中启动 EXE，并传入以下参数：

```text
sample-analyzer.exe --case <case-id> --input <source-path> --output <output-path>
```

参数含义：

| 参数 | 含义 |
| --- | --- |
| `--case` | 工作台生成的案例 ID。插件可以把它写入诊断信息，但不要假设它是文件名。 |
| `--input` | 当前案例的原始日志压缩包绝对路径。 |
| `--output` | 当前案例的报告输出目录绝对路径。 |

插件必须遵守以下约定：

1. 成功时退出码为 `0`。
2. 成功时在 `--output` 目录直接生成 `report.html`。
3. 报告引用的静态资源放在 `--output` 目录内，并使用相对路径。
4. 失败时返回非零退出码，并将可供工程师理解的原因写到标准错误输出。
5. 不要依赖工作台的当前进程目录；入口进程的工作目录是插件自身目录。
6. 不要修改其他案例、工作台数据库或用户配置文件。

工作台会检查 `report.html` 是否存在。即使 EXE 返回 `0`，如果报告文件缺失，任务也会标记为失败。

一个最简单的兼容实现可以是：读取 `--input`，分析后创建 `--output`，写入 `report.html`，最后返回 `0`。报告可以是静态 HTML，也可以引用同一输出目录中的资源。

## 5. 现有旧版插件协议

当前随程序提供的 `log_analyzer.exe` 使用旧版协议，清单如下：

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

设置 `runner` 后，工作台会使用兼容命令：

```text
log_analyzer.exe -d <source-path> -o <report-output-directory>
```

`-d` 保留原有输入参数；插件继续在原始输入文件同名目录下生成解压内容，`-o` 用于指定工作台报告目录。插件必须在输出目录中生成 `report.html` 以及它引用的 `static`、`structured` 等资源。未传入 `-o` 时，仍使用原始的 `report/report.html` 默认位置。该协议用于适配当前日志分析插件；新插件应使用第 4 节的标准协议。

## 6. SDK 和 DLL 契约

`src/HephaestusWorkbench.PluginSDK/PluginContracts.cs` 定义了未来 DLL 插件使用的 `IAnalysisPlugin` 和执行上下文：

- `CaseId`：案例 ID。
- `SourcePath`：原始日志路径。
- `OutputPath`：报告输出目录。
- `ExtractPath`：案例解压目录。
- `WorkingDirectory`：插件工作目录。

当前生产代码没有加载 DLL 的实现，因此不要仅凭实现 `IAnalysisPlugin` 就认为插件能够运行。等 DLL runner 正式接入后，应同步更新本文件、插件中心状态和回归测试。

## 7. 本地安装和验收

建议按以下顺序验收：

1. 创建独立插件目录，并放入 EXE 和 `manifest.json`。
2. 使用命令行单独运行 EXE，确认参数解析、退出码和 `report.html` 输出。
3. 将插件目录复制到 `<数据目录>/Plugins/`。
4. 在插件中心点击“刷新插件”，确认名称、版本、类型和入口路径正确。
5. 将一个有效的 `.tgz` 日志放入收件目录，创建案例并执行分析。
6. 在报告中心打开生成的报告，确认 HTML 及其静态资源均可加载。
7. 人为制造入口缺失或清单格式错误，确认插件中心显示问题，且其他有效插件仍能被发现。

## 8. 兼容性注意事项

- 不要把插件输出写到程序安装目录；所有案例结果应只写入 `--output` 或插件自己的临时目录。
- 不要依赖未记录的命令行参数、环境变量或工作台内部数据库表。
- 清单字段属于跨版本边界，新增字段应保持旧字段可用。
- `report.html` 是工作台报告查看器的稳定入口；修改入口文件名需要同时修改工作台运行器和测试。
- 插件异常信息应明确说明原因，避免只返回“失败”。
