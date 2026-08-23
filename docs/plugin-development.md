# Hephaestus Workbench v2 扩展开发文档

本文描述 Hephaestus Workbench **v2.0.0** 的扩展契约。v2 不兼容旧插件目录、旧 manifest、第三方 DLL/WPF 扩展或旧报告入口文件。

## 1. 扩展契约与当前宿主状态

| kind | runtime.kind | capability | 当前状态 |
| --- | --- | --- | --- |
| `workspace` | `web` | `workspace.page` | 已由固定 `WorkspaceHostWindow` 承载 |
| `analysis` | `process` | `analysis.engine` | 已通过 `analysis-process-v1` 执行日志分析 |
| `analysis` | `content` | `analysis.rule-pack`、`analysis.report-template` | 只有 manifest 组合校验；没有内容 DTO/schema，也没有宿主应用流程 |
| `maintenance` | `content` | `maintenance.workflow-pack`、`maintenance.command-profile` | 已有 Workflow/Command Profile DTO；内容加载和 Planner/Policy/Executor 尚未实现 |

不支持：

- 第三方 DLL、WPF View 或 ViewModel。
- 扩展贡献侧栏导航、排序、分组或默认固定项。
- unsigned package、developer mode 或绕过发布者信任。
- manifest 指定任意报告入口。

## 2. manifest v2

Analysis Process 扩展示例：

```json
{
  "schemaVersion": 2,
  "id": "log-analyzer",
  "name": "日志分析",
  "version": "2.0.0",
  "kind": "analysis",
  "publisherId": "thelinyue",
  "hostApiVersion": "1.0",
  "minHostVersion": "2.0.0",
  "runtime": {
    "kind": "process",
    "protocol": "analysis-process-v1",
    "entry": "bin/log-analyzer.exe"
  },
  "capabilities": [
    "analysis.engine",
    "analysis.scope.comprehensive"
  ],
  "permissions": [],
  "dependencies": []
}
```

Workspace Web 扩展示例：

```json
{
  "schemaVersion": 2,
  "id": "rule-editor",
  "name": "规则编辑器",
  "version": "2.0.0",
  "kind": "workspace",
  "publisherId": "thelinyue",
  "hostApiVersion": "1.0",
  "minHostVersion": "2.0.0",
  "runtime": {
    "kind": "web",
    "entry": "web/index.html"
  },
  "capabilities": ["workspace.page"],
  "permissions": [],
  "dependencies": []
}
```

约束：

- `id`、版本、发布者、类别和能力必须与 Catalog release 一致。
- `runtime.entry` 必须是扩展版本目录内的相对路径，禁止绝对路径、路径穿越和重解析点逃逸。
- `hostApiVersion` 当前固定为 `1.0`。
- `minHostVersion` 使用 SemVer。
- manifest 不得包含 `navigation`、`order`、`group`、`pinned`、旧运行器字段或旧报告入口字段。

## 3. 日志分析能力

分析中心只使用日志分析扩展，不提供分析引擎选择器。分析范围由用户选择：

- `comprehensive`：综合分析，即当前日志分析报告。
- `storage`：存储分析，由后续日志分析扩展版本提供。

扩展必须通过 capability 声明支持范围：

```text
analysis.scope.comprehensive
analysis.scope.storage
```

宿主只会选择已启用、版本兼容且具有 `analysis.engine` 和对应范围 capability 的唯一扩展。零匹配或多匹配都会拒绝创建 Case/Task，并显示中文错误。

## 4. analysis-process-v1

宿主以独立进程启动扩展，通过标准输入发送一份 JSON 请求，并从标准输出读取一份 JSON 结果。DTO 定义位于：

```text
src/HephaestusWorkbench.PluginSDK/AnalysisProcessProtocol.cs
```

请求字段与 `AnalysisProcessRequest` 严格一致：

- `protocol`
- `requestId`
- `caseId`
- `sourcePath`
- `outputDirectory`
- `extractDirectory`
- `rulesPath`（可选）
- `scope`

响应字段与 `AnalysisProcessResponse` 严格一致：

- `protocol`
- `requestId`
- `succeeded`
- `errorCode`（失败时必填）
- `errorMessage`（失败时必填）

成功响应不能包含错误字段；失败响应必须同时包含 `errorCode` 和 `errorMessage`。协议不接受其他响应字段。

要求：

1. 成功时退出码为 `0`。
2. 扩展必须生成 `outputDirectory/index.html`；宿主保证并复核 `outputDirectory` 等于 `extractDirectory/Report`。
3. 不生成或兼容旧版报告入口文件。
4. 失败时返回非零退出码。非零退出码时，宿主仍优先解析 stdout 中协议合法、`requestId` 匹配且 `succeeded=false` 的结构化响应；无法解析时才回退到标准错误或通用退出码信息。
5. 不访问其他 Case、工作台数据库、凭据或扩展 Registry。
6. 进程启动后的具体扩展版本由版本租约固定，运行期间不会切换到新版本。

快速单日志分析成功后宿主会用系统默认浏览器打开报告；批量和监控目录分析只更新状态，不批量打开浏览器标签。

## 5. Workspace Web 扩展

Workspace 扩展只能从扩展中心点击“打开”，由固定 `WorkspaceHostWindow` 承载，不注册侧栏入口。

安全默认值：

- 使用独立 WebView2 profile 和固定虚拟来源。
- 禁止外部网络、跨源导航、新窗口、下载、外部 URI Scheme 和浏览器权限。
- 禁止任意文件系统、Shell、进程和系统 API。
- Web Message 必须来自当前虚拟来源并符合版本化 JSON-RPC：`requestId/method/params`。
- Bridge 方法必须同时通过 manifest permission 和发布者 trust scope；v2.0.0 当前未开放 Workspace Bridge 方法。

页面必须把全部 HTML、CSS、JavaScript、字体和图片打包在扩展目录中，不得使用 CDN。

## 6. Content 与 Maintenance 预留状态（宿主尚未实现）

这两类扩展目前都不能作为 v2.0.0 可用功能发布：

- Analysis Content 目前只有 manifest 的 kind/runtime/capability 组合校验；**没有 Analysis Content 的内容 DTO/schema**，也没有规则包或报告模板的宿主应用流程。
- Maintenance Content 已有预留 Workflow Definition / Command Profile DTO，但没有内容加载、Planner、Policy 或 Executor。

Maintenance 定义只能声明高层 action、结构化参数 token、目标类型和步骤。宿主负责策略、目标唯一性、最终命令、确认、执行和审计。禁止自由命令字符串、`sh -c`、反引号、`$()`、管道和重定向。

后续完成 Maintenance Host 后，首个可发布范围仍只允许只读发现、Preflight、不可变计划、确认、执行输出和审计框架；真实自动修复或破坏性 Runbook 不在当前实现中。

## 7. Catalog、签名与安装

Catalog 使用 schema v2。Release 至少包含：

- URL
- ZIP 大小
- SHA-256
- Ed25519 `keyId/signature`
- `minHostVersion`

签名覆盖原始 ZIP 字节。Catalog 提供的公钥不会自动获得信任；宿主内置信任表决定 `keyId → publisherId → allowedKinds/permissions`。

安装事务：

```text
下载
→ size / SHA-256 / Ed25519
→ 同盘 staging 解压
→ manifest / schema / path / Host API 校验
→ 类型健康检查
→ 原子移动版本目录
→ current.json = pending
→ 正式加载验证
→ current.json = healthy
```

版本目录：

```text
Extensions/
└─ <extension-id>/
   ├─ <version>/
   ├─ current.json
   └─ current.json.bak
```

加载失败会回滚；运行任务持有版本租约；至少保留 active 和 rollback 两个版本。相同 ID/版本对应不同 ZIP 内容时拒绝安装。

## 8. 开发与发布验收

1. 使用 PluginSDK v2 DTO 和验证器生成 manifest。
2. 为 Analysis Process 编写 request/response、取消、失败退出码和 `Report/index.html` 测试。
3. 为 Workspace 页面验证离线资源、同源消息和无网络依赖。
4. 运行扩展仓测试并打包 ZIP。
5. 计算 size 与 SHA-256。
6. 使用 CI Secret 中的 Ed25519 私钥签名原始 ZIP，并反向验签。
7. 发布资产和 Catalog release；不要把私钥、占位签名或动态“远端最新版本”写入仓库。
8. 在全新 v2 工作区验证安装、启用、运行、版本租约、回滚和默认浏览器报告打开。
