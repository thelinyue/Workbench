# 应用中心发布流程

工作台只发布宿主和 App Host API；分析中心发布独立 ZIP，不改变工作台版本号。官方应用目录仓库为 `thelinyue/Workbench-Apps`，目录文件保持严格的 `AppCatalogDocumentV1` 结构。

## 发布分析中心

1. 修改 `apps/analysis-center/manifest.json` 的 SemVer 版本，并确认 `hostApiVersion` 与 `minWorkbenchVersion`。
2. 在发布环境配置 `HEPHAESTUS_APP_SIGNING_PRIVATE_KEY` 和 `HEPHAESTUS_APP_SIGNING_KEY_ID`，私钥只存放在 CI Secret；工作台安装端通过 `HEPHAESTUS_APP_TRUSTED_KEYS_FILE` 或 `HEPHAESTUS_APP_TRUSTED_KEYS_JSON` 配置对应的 Ed25519 公钥。
3. 执行 `npm run typecheck:analysis-center`、`npm run test:analysis-center` 和 `npm run build:analysis-center`。没有签名私钥时构建脚本会生成 ZIP，但明确标记为不可安装的检查产物。
4. 将 `dist/analysis-center-vX.Y.Z.zip` 和 `dist/release.json` 上传到 `Workbench-Apps` 的同名 GitHub Release；使用 `node tools/update-app-catalog.mjs catalog.json dist/release.json` 写入 Catalog 后提交。

当前官方目录仓库为公开的 `thelinyue/Workbench-Apps`，发布工作流还需要 Workbench 仓库中的 `APPS_RELEASES_TOKEN` Secret。该 Token 只应授予该 Apps 仓库的 Contents 读写权限；不要把个人登录 Token 写入仓库 Secret。

发布流水线必须拒绝重复版本、缺少签名、非 HTTPS 下载地址、SHA-256 不一致和未通过 `parseAppCatalog` 的 Catalog。工作台在线目录失败时只使用最后一次有效缓存，已安装版本仍可启动。

应用构建使用固定 ZIP 文件顺序、时间和权限元数据；同一源码的重复构建必须产生相同 ZIP SHA-256。

## 工作台种子包

`npm run package:win` 会先构建分析中心 ZIP，再由 electron-builder 作为 `extraResources` 放入安装包。首次启动只会尝试通过同一套签名、哈希、manifest、ZIP 路径和兼容性校验安装种子包；没有受信公钥或签名无效时会输出中文错误并保留可运行的工作台壳层。

## Host API 能力

分析中心 renderer 只能通过 iframe `postMessage` 调用 Host API。当前宿主能力包括选择诊断包、打开报告和定位文件；主进程按 manifest 的 capability 白名单二次校验，应用不能直接访问 Electron、Node 或任意本机路径。

## 发布 LVM 缓存清理工具

LVM 缓存清理工具是纯 Web 应用，不启动 backend Worker。它在工作台 iframe 内本地解析 LVM2 VG 文本，支持文件选择、文本粘贴、结果预览和保存清理结果，不执行任何真实 LVM 命令。

1. 修改 `apps/lvm-uncache-tool/manifest.json` 的 SemVer 版本。
2. 执行 `npm test`、`npm run typecheck` 和 `npm run build:lvm-uncache-tool`。
3. 确认 `dist/release.json` 已包含签名，再将 ZIP 和 release.json 发布到 `Workbench-Apps` 的 `lvm-uncache-tool-vX.Y.Z` Release。
4. 使用 `node tools/update-app-catalog.mjs catalog.json apps/lvm-uncache-tool/dist/release.json` 更新目录。

LVM 应用只申请 `file.save`，保存动作由宿主弹出文件对话框并校验文件名、大小和覆盖选项；应用页面不能访问本机路径或执行命令。

当前 `lvm-uncache-tool-v1.0.0` 已发布到 `thelinyue/Workbench-Apps`。该版本的 ZIP、`release.json`、SHA-256 和 Ed25519 签名均视为不可变发布基线；工作流发现同名 Release 时只做内容一致性校验，不覆盖既有资产。后续行为变更必须递增应用版本号。

LVM 应用安装成功后由应用注册表驱动显示桌面图标；未安装时只在应用中心和应用库中显示，不会占用桌面槽位。
