# Hephaestus Workbench Releases

**v2.0.0 尚未发布。** 当前源码树没有提交真实 `distribution/bundled-extensions.json`，宿主正式发布者公钥信任锚也尚未注入，因此不能声称已有可安装的 v2.0.0 正式产物。

正式发布完成后，对应 Release 应包含：

```text
HephaestusWorkbench_v2.0.0.exe
SHA256SUMS.txt
```

届时可使用 PowerShell 校验：

```powershell
Get-FileHash -Algorithm SHA256 .\HephaestusWorkbench_v2.0.0.exe
```

结果必须与同一 Release 的 `SHA256SUMS.txt` 一致。

## v2 数据边界

v2.0.0 不兼容旧数据库、旧配置、旧 `Plugins`、旧 manifest/catalog、旧 `report.html` 或旧客户端。发现旧工作区时，客户端只显示旧目录绝对路径以及“打开目录 / 退出”，不会迁移、备份或删除数据。

## 正式扩展校验链路

发布候选安装包必须携带仓库锁定清单声明的真实签名扩展资产。客户端将校验：

- schema v2、扩展身份与 Host API；
- ZIP 大小和 SHA-256；
- 宿主内置信任锚与 Ed25519 原始 ZIP 签名；
- 解压路径、manifest、类型健康检查和激活回滚。

在线扩展目录来自独立 `Hephaestus-Workbench-Plugins` 仓库的 schema v2 `catalog.json`。Catalog 只能声明发布元数据，不能自行授予公钥信任。

遇到安装或校验错误时，请保留工作台数据目录中 `Logs/workbench.log` 的中文错误信息。
