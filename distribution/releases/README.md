# v2 正式发行说明

私有源码标签 `vX.Y.Z` 触发候选安装包构建。发布前必须在本目录增加同名的 `vX.Y.Z.md`，工作流只使用该文件作为公开 Release 说明；缺失时以中文错误停止。

正式安装包还必须具备：

- `distribution/bundled-extensions.json` 真实锁定清单；
- 清单引用的不可变签名扩展 ZIP；
- 对应的 size、SHA-256、Ed25519 keyId/signature；
- 客户端内置的真实发布者公钥信任锚。

任何一项缺失时保持 fail-closed，不得用测试密钥、占位签名、动态远端最新版本或未签名开发模式替代。

## 受保护发布门禁

`build` job 只生成候选安装包和烟测证据模板。公开发布由独立 `publish` job 完成，并绑定 GitHub Environment：

```text
workbench-production
```

仓库管理员必须为该 Environment 配置 **required reviewers**。审批人只有在 Windows 10/11 x64 人工烟测都有记录时才能批准：

1. Windows 10 x64 全新安装、首次启动、离线 Bundle 健康、默认浏览器报告、卸载通过。
2. Windows 11 x64 全新安装、首次启动、离线 Bundle 健康、默认浏览器报告、卸载通过。
3. Credential Manager、WebView2 Runtime、SSH 基础连接和安装包 SHA-256 复核通过。

候选 Artifact 与正式发布运行在不同 job；正式 job 会重新下载并核对安装包和 `SHA256SUMS.txt`，并精确拒绝已存在或状态无法确认的 Release。

手工运行工作流只生成保留 14 天的候选 Artifact，不能创建公开 Release。正式发布只能由不可变的 vX.Y.Z 标签触发，并在受保护的 workbench-production Environment 完成人工审批后执行。

`RELEASES_TOKEN` 必须保存为 `workbench-production` Environment Secret。它应是细粒度令牌，只对 `thelinyue/Hephaestus-Workbench-Releases` 授予 Contents 读写权限，不应授予私有源码仓库写权限。
