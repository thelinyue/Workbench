# Hephaestus Workbench v2 分发说明

> 当前状态：v2 发布链路尚未完成。本文件描述正式版必须满足的边界，不代表当前仓库已经可以发布 v2.0.0。

Hephaestus Workbench 主仓保持私有。安装包、扩展资产、校验文件和用户文档发布到公开 Releases 仓库；扩展 Catalog、Schema、投稿模板和校验工具发布到公开扩展目录仓。公开仓库不得包含产品源码、PDB、测试日志、内部路径、私钥、令牌或 CI 配置。

## 正式构建边界

v2 安装包必须使用 `BundledExtensions/` 携带离线扩展，并由 `distribution/bundled-extensions.json` 锁定每个扩展的：

- 扩展 ID 与独立版本。
- ZIP 资产文件名与固定来源。
- 原始 ZIP 字节大小和 SHA-256。
- Ed25519 `keyId/signature`。
- 发布者身份与允许的 kind/permission 范围。

发布过程不得动态选择远端最高版本。离线扩展必须走与在线安装相同的大小、SHA-256、Ed25519、manifest、路径、Host API、健康检查、版本目录和激活事务，不能复制为特殊旁路文件。

当前仓库仍需完成安装器脚本、Release Workflow、正式信任锚和锁定清单改造。在这些工作完成并通过验证前，不得把现有流水线产物作为 v2.0.0 正式版发布。

## 签名与资产

- Ed25519 私钥只来自 CI Secrets，不进入任何仓库、安装包、日志或构建缓存。
- 主仓只内置正式公钥和受限 trust scope；Catalog 声明的公钥不能自行获得信任。
- 构建候选必须使用清单固定的真实 Release 资产，并在下载后重新计算大小、SHA-256 和签名。
- 相同扩展 ID/版本对应不同 ZIP 内容时立即拒绝构建或安装。
- 扩展版本独立于 Workbench 版本，不得用主程序版本替代扩展版本。

## 发布顺序

1. 在扩展源码仓执行测试、打包、签名和反向验签。
2. 发布固定版本扩展资产。
3. 更新公开扩展目录仓的 schema v2 Catalog，并验证 URL、大小、SHA-256 与签名。
4. 更新主仓 `distribution/bundled-extensions.json`，锁定本次安装包携带的资产。
5. 执行主仓 Restore、Release Build 和全量 Test。
6. 构建候选安装包，完成 Windows 10/11 x64 全新安装、离线扩展、默认浏览器、Credential Manager 和 WebView2 Runtime 烟测。
7. 仅在候选验证通过后发布 v2.0.0 Release 和 `SHA256SUMS.txt`。

v2.0.0 不执行升级、降级、旧数据迁移或旧扩展兼容测试。安装与卸载不得删除用户数据目录。

## 公开仓库边界

- 扩展目录仓保存 Catalog v2、Schema、模板和校验工具，不保存私钥。
- 扩展源码仓保存各扩展源码和发布流水线，不将扩展版本绑定到 Workbench 版本。
- Releases 仓保存经过验证的扩展 ZIP、Windows 安装包、校验文件、发行说明和分发许可。

发布后必须使用未登录请求重新下载公开资产，复算哈希并验证签名；发现资产内容、清单或 Catalog 不一致时停止发布。
