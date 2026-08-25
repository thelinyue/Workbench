# Electron 分发说明

Hephaestus Workbench 的源码仓库保持私有。当前发布物是 Electron Windows 安装包。

## 本地构建

```powershell
npm ci
npm run typecheck
npm test
npm run package:win
```

安装包由 electron-builder 生成到 `release` 目录，名称为：

```text
HephaestusWorkbench_v<package.json.version>.exe
```

安装器使用 NSIS，支持选择安装目录、桌面/开始菜单快捷方式、升级和卸载。卸载默认不删除 Electron `userData` 中的工作台数据。

## 正式构建

GitHub Actions 从 `package.json` 读取版本。推送 `vX.Y.Z` 标签时，标签必须与 `package.json.version` 完全一致；手工运行工作流也使用该版本，不接受独立的版本覆盖。

工作流依次执行：

1. `npm ci` 安装锁定依赖。
2. 运行 TypeScript 类型检查和 Vitest。
3. 运行 `electron-vite build` 生成主进程、preload 和 renderer 产物。
4. 使用 electron-builder 生成 Windows NSIS 安装包。
5. 只将安装包和 `SHA256SUMS.txt` 作为公开资产，并进行匿名下载校验。

## 发布安全边界

- 不上传源码、测试日志、内部路径、数据库、密钥或构建缓存。
- 公开资产只允许安装包和对应 SHA-256 校验文件。
- 用户数据位于安装目录之外，升级和卸载不能主动删除工作台数据。
- 当前 Electron 版本使用内置 TypeScript 分析逻辑，不再注入旧版外部插件二进制。
