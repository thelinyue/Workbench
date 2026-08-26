# 应用中心协作说明

应用源码、独立构建和应用发布统一在 [Workbench-Apps](https://github.com/thelinyue/Workbench-Apps) 完成。Workbench 只消费 `AppManifestV1`、`AppCatalogDocumentV1` 和签名 Release，不再提供历史目录协议或插件 manifest 运行入口。

## Workbench 消费应用

1. Workbench 读取 `Workbench-Apps/catalog.json`；
2. 应用中心选择兼容当前 Workbench 和 Host API 的版本；
3. 安装器下载 ZIP 和 `release.json`；
4. 主进程校验 HTTPS、大小、SHA-256、Ed25519 签名、manifest 和宿主兼容版本；
5. 通过 App Host API 启动应用并隔离应用 backend。

分析中心首次随 Workbench 安装包提供签名种子包，后续更新仍从 `Workbench-Apps` Release 安装。规则编辑器通过版本化 `rules.*` Host API 读取、校验、保存、提交和导出用户规则。

## 维护边界

- 需要修改应用 UI、backend、规则或应用版本时，只修改 `Workbench-Apps`；
- 需要修改 Host API、应用安装器、桌面壳或主程序安装包时，修改 `Workbench`；
- 两个仓库共享的唯一运行协议是 `AppManifestV1`、`AppCatalogDocumentV1` 和版本化 Host API；
- 正式发布必须使用 CI Secret 进行签名，缺少私钥时只能进行构建检查，不能把无签名记录写入目录。
