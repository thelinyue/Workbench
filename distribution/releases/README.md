# 自动发行说明

私有源码标签 `vX.Y.Z` 触发自动发布前，必须在本目录增加同名的 `vX.Y.Z.md`。工作流只读取该文件作为公开 Release 说明；缺失时会以中文错误终止，不会生成或覆盖公开 Release。

手工运行工作流默认只生成保留 14 天的 Artifact。只有明确勾选“同时发布到公开 Releases 仓库”时才会使用 `RELEASES_TOKEN` 发布。

仓库管理员必须配置名为 `RELEASES_TOKEN` 的 Actions Secret。它应是细粒度令牌，仅对 `thelinyue/Hephaestus-Workbench-Releases` 授予 Contents 读写权限，不应授予私有源码仓库写权限。
