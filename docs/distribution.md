# Electron 分发说明

Workbench 的正式安装包发布在当前 `Workbench` 仓库的 GitHub Releases。应用独立包发布在 [Workbench-Apps](https://github.com/thelinyue/Workbench-Apps) 的 GitHub Releases。

## 本地构建

```powershell
npm ci
npm run typecheck
npm test
npm run package:win
```

安装包由 electron-builder 生成到 `release` 目录，名称为 `Workbench_v<package.json.version>.exe`。打包前会运行 `npm run fetch:seed-app`，从 `Workbench-Apps` 正式 Release 下载分析中心和 SSH 终端的签名种子包并执行以下检查：

- HTTPS 下载地址；
- SemVer、宿主 API 和最低 Workbench 版本；
- ZIP 大小和 SHA-256；
- Ed25519 签名和受信任公钥。

任一检查失败都会阻止安装包构建，并输出中文错误。

## 正式构建

推送与 `package.json.version` 完全一致的 `vX.Y.Z` 标签后，工作流依次执行：

1. `npm ci` 安装锁定依赖；
2. 运行类型检查和测试；
3. 下载并校验分析中心和 SSH 终端种子包；
4. 构建 Electron 主程序和 Windows NSIS 安装包；
5. 生成仅包含安装包与 `SHA256SUMS.txt` 的公开资产目录；
6. 发布到当前仓库并匿名下载复核。

工作流使用当前仓库内置的 `contents: write` 权限，不需要跨仓库发布 Token。

## 安全边界

- 不上传源码、测试日志、内部路径、数据库、密钥或构建缓存；
- 应用源码和应用 ZIP 不混入 Workbench Release；
- 用户数据位于安装目录之外，升级和卸载不能主动删除工作台数据；
- 公开前必须完成密钥、内部路径和敏感文件审查。
