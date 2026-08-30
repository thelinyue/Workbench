# Task 5 报告：托盘驻留、退出清理、打包资源与文档

## 范围

- 新增 `src/main/services/workbench-tray-controller.ts` 及单元测试。
- 主窗口普通 close 转为 `preventDefault()` + `hide()`，second-instance、activate、托盘 click 和“打开工作台”统一复用 `restoreMainWindow()`。
- Windows/Linux 的 `window-all-closed` 保持托盘驻留；托盘“退出”只调用 `app.quit()`，继续进入现有 before-quit 异步 gate。
- 退出清理顺序保持 runtime drain 后依次关闭主窗口、应用窗口、托盘、协议和仓储；共享既有并发清理 Promise。
- `registerWorkbenchIpc` 注入 `appWindowManager.closeApp(appId)`；打包资源复制到 `tray/app-icon.ico`，并更新 `docs/module_map.md`。
- 未修改 manifest、Catalog、Host API、依赖或发布流程。

## RED/GREEN

### RED

先新增 `tests/main/workbench-tray-controller.test.ts` 并修改生命周期用例，运行：

```text
npm test -- tests/main/workbench-tray-controller.test.ts tests/main/workbench-lifecycle.test.ts
```

结果为预期失败：托盘测试因 `workbench-tray-controller` 模块不存在失败；生命周期测试显示普通 close 未调用 `preventDefault()`、window-all-closed 仍调用 quit（2 个失败断言）。其余原有生命周期用例通过。

### GREEN

完成最小实现后重新运行同一命令：

```text
Test Files  2 passed (2)
Tests       12 passed (12)
```

## Fix round 1

审查发现两个生命周期边界：退出清理开始后恢复入口仍可能显示窗口；托盘创建失败后主窗口仍会隐藏，导致进程无可恢复入口。按 TDD 先补用例并运行目标测试，预期 RED 为 3 个失败（恢复仍 show、`isAvailable()` 缺失、不可用托盘 close 仍 preventDefault）。

最小修复为：`restoreMainWindow()` 在 shutdown 开始或完成后直接返回；托盘 controller 增加 `isAvailable()`；主窗口 close 通过注入的托盘可用性 callback 判断，不可用时直接请求 `app.quit()`；同时删除已无用途的 `platform/getNativeWindowCount` 参数。

修复后目标测试：

```text
Test Files  2 passed (2)
Tests       15 passed (15)
```

## 验证

- `npm test`：49 个测试文件通过，284 个测试通过。
- 修复轮目标测试：2 个测试文件通过，15 个测试通过。
- `npm run typecheck`：通过，退出码 0。
- `npm run build`：Electron main、preload、renderer 均构建成功，退出码 0。
- `D:\code\Workbench-Apps\npm run test:analysis-center`：38 个测试文件通过，181 个测试通过。
- `D:\code\Workbench-Apps\npm run typecheck:analysis-center`：通过，退出码 0。
- `git diff --check`：通过；额外确认 `assets/app-icon.ico` 存在且 `package.json` 映射为 `tray/app-icon.ico`。

## 自审

- 托盘 click 与“打开工作台”菜单保存同一个恢复 callback；菜单 Exit 没有 destroy 路径。
- shutdown 开始或完成后恢复入口 no-op，不会创建、显示或聚焦窗口；托盘不可用时 close 不 hide，改走 app.quit 的 before-quit gate。
- lifecycle、index 与测试装配已删除未使用的 `platform/getNativeWindowCount` 参数。
- before-quit 首次进入即标记 shutdown，普通 close handler 在退出期间不再阻止 `destroy()`；现有 `destroyMainWindowForShutdown()` 仍先解除跟踪再强制销毁。
- 托盘清理位于 runtime/app window 之后、协议和仓储之前；既有 `createOrderedCleanup` 继续串行并共享 Promise。
- 打包图标严格解析为 `process.resourcesPath/tray/app-icon.ico`，开发图标解析为应用目录 `assets/app-icon.ico`。
- 新增类、恢复/隐藏与退出顺序均有中文设计注释；托盘创建和销毁异常输出中文日志。

## 提交

- 提交 SHA：fde9a49（修复实现）；报告元数据提交见最终提交

## 关注点

- 本次未执行实际 Windows NSIS 安装包构建或桌面 Electron 手工交互；资源映射、路径分支和托盘行为由单元测试及构建检查覆盖。
