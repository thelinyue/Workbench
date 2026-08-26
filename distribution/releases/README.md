# Workbench 自动发行说明

推送 `vX.Y.Z` 标签时，标签必须与 `package.json.version` 完全一致，并在本目录提供同名的 `vX.Y.Z.md`。GitHub Actions 会把 Windows 安装包和 `SHA256SUMS.txt` 发布到当前 Workbench 仓库的 GitHub Releases。

工作流使用当前仓库的 `contents: write` 权限，不使用旧 Releases 仓库地址或跨仓库发布令牌。缺少发行说明、版本不一致、种子包校验失败或安装包资产不完整时，流程会以中文错误终止。
