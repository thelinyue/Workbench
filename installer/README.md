# Hephaestus Workbench v2.0.0 安装包

正式发行使用 Inno Setup 6 生成 Windows x64 单文件离线安装包，默认安装目录：

```text
C:\Program Files\HephaestusWorkbench
```

## 构建命令

```powershell
.\installer\build-installer.ps1 `
  -Configuration Release `
  -Version 2.0.0
```

构建机必须安装 Inno Setup 6，也可以通过 `-InnoCompilerPath` 显式传入 `ISCC.exe`。

正式构建固定读取：

```text
distribution/bundled-extensions.json
```

该文件必须是 schema v2 的真实锁定清单，并引用已经发布的不可变签名扩展 ZIP。没有真实资产时应保持文件缺失，让正式安装包构建明确失败；不得提交测试密钥、占位公钥、占位签名或伪造资产。

## 从扩展 Release 生成 Bundle 锁定清单

从扩展 Release 下载**明确版本**的 final ZIP 和同版本 `release-metadata.json`。metadata 必须为 `schemaVersion: 2`。最终 ZIP 根级唯一的 `manifest.json` 才是扩展身份和运行时契约的权威源；`release-metadata.json` 仅承担跨仓机器交接，不含公钥，也不建立信任。

人工审核 description 后，在主仓根目录运行：

```powershell
.\installer\import-release-metadata.ps1 `
  -ReleaseMetadataPath <release-metadata.json> `
  -PackagePath <final-zip> `
  -ExtensionId log-analyzer `
  -ReviewedDescription "..." `
  -OutputPath .\distribution\bundled-extensions.json
```

导入脚本会复核根级唯一 manifest、metadata 与 ZIP manifest 的完整一致性、实际文件名、size 和 SHA-256，随后写出锁定清单。导入阶段不验签；正式 `build-installer.ps1` 使用主仓 trust anchor，对固定 HTTPS URL 下载的 ZIP 按 `size → SHA-256 → Ed25519` 验证。

不得使用 `latest`、假 ZIP、假签名、测试密钥，或手写、猜测 manifest/description。description 必须经人工审核并显式传入。若尚无真实 `distribution/bundled-extensions.json`，正式安装包必须失败，不能据此声称可以发布。

Bundle 仅保证 v2.0.0 客户端首次安装时离线可用。客户端上线后，扩展中心从固定 Catalog 按扩展自身版本独立在线发现和更新；无需发布新客户端，客户端版本更新也不会自动改变已安装扩展版本。在线更新与 Bundle 共用 size、SHA-256、Ed25519、manifest、健康检查和激活回滚链路。`AllowPrerelease` 是唯一用户级预发布策略，且不会因开启而自动将 Stable 扩展升级为 Prerelease。

## 固定构建链路

```text
读取锁定清单
→ 校验 schema、asset 文件名、size、SHA-256 和 Ed25519 签名字段
→ 按清单中的固定 HTTPS URL 下载 ZIP
→ 复核实际 size/SHA-256，并依据主仓 trust anchor 验证 Ed25519
→ 发布 RequireBundledExtensions=true 的 self-contained 主程序
→ 写入 App/BundledExtensions
→ Inno Setup 递归封装完整 AppSource
→ 生成安装包和 SHA256SUMS.txt
```

安装器不会查询远端“最新版本”或 `latest`，不会展开扩展 ZIP，也不会复制单个插件 EXE。客户端首次启动时使用与在线安装相同的 Trust Store、Ed25519 验签、暂存、健康检查、激活和回滚流程部署 Bundle。

## 正式版边界

- 只生成 `HephaestusWorkbench_v<版本号>.exe` 和 `SHA256SUMS.txt`。
- 不发布 Update、Uninstall 或额外 ZIP 安装介质。
- 不兼容 v1 数据、配置、Plugins、manifest/catalog 或报告入口。
- 安装程序不迁移、不备份、不删除用户旧数据；客户端发现旧工作区时会阻止启动并提示用户手工处理。
- 签名私钥只存在于扩展发布 CI Secrets，不进入主仓或安装包构建脚本。
