# Workbench

Workbench 是 Windows 上的 Electron 工作台宿主。它负责桌面壳、应用中心、App Host API、应用安装/运行时、主程序测试和正式版安装包发布。

应用源码、应用目录和应用独立版本发布位于 [Workbench-Apps](https://github.com/thelinyue/Workbench-Apps)。Workbench 不再维护应用源码，也不再使用旧插件目录或独立 Releases 仓库。

## 仓库职责

- `Workbench`：Electron 主程序、Host API、桌面布局、应用安装与运行时、主程序测试、Windows 安装包和 SHA-256 发布资产。
- `Workbench-Apps`：分析中心、LVM 缓存清理工具、SSH 终端、分析规则编辑器的源码、`AppManifestV1`、`AppCatalogDocumentV1` 和独立 Release。

分析中心是日志分析能力的唯一实现。Workbench 打包时从 `Workbench-Apps` 的正式 Release 下载签名种子包，校验版本、大小、SHA-256、Ed25519 签名和宿主兼容性后嵌入安装包。

## 开发与验证

```powershell
npm ci
npm run typecheck
npm test
npm run build
```

制作 Windows 安装包：

```powershell
npm run package:win
```

`package:win` 要求能够匿名下载已签名的分析中心种子包；下载失败、哈希错误、签名错误或版本不兼容都会以中文错误终止打包。

## 用户数据

宿主数据库只保存桌面图标布局。应用的诊断包、任务、报告和规则数据由各自应用 backend 保存到应用数据目录。渲染进程只能通过 preload 暴露的 `window.workbench` 接口访问宿主能力。

## 发布

推送与 `package.json` 版本一致的 `vX.Y.Z` 标签后，GitHub Actions 会完成类型检查、测试、Electron 构建、Windows 安装包构建，并把安装包和 `SHA256SUMS.txt` 发布到当前 `Workbench` 仓库的 GitHub Releases。正式发布流程见 [Electron 分发说明](docs/distribution.md)。

错误信息和任务失败原因使用中文，便于非开发者部署和排查。
