# Task 2 实施报告

## 范围

- 新增 `src/main/services/app-lifecycle-coordinator.ts`：按 `appId` 串行编排冷启动、启停、更新和卸载；不同应用可并行。
- 新增 `src/main/services/app-package-uninstaller.ts`：校验绝对 `appsRoot` 和安全 `appId`，支持保留或删除应用私有数据。
- 修改 `AppWindowManager`：增加 `closeApp(appId)`，等待目标窗口 opening 结算后保存状态并销毁目标窗口。
- 新增协调器、卸载器测试，并补充窗口收口测试。

## TDD 证据

### RED

首次新增测试后运行：

```text
npm test -- tests/main/app-window-manager.test.ts tests/main/app-package-uninstaller.test.ts tests/main/app-lifecycle-coordinator.test.ts
```

结果：3 个测试文件失败。窗口文件新增用例因 `closeApp is not a function` 失败；协调器和卸载器测试因对应生产模块尚不存在而无法加载。这证明新增行为不是既有实现覆盖的。

### GREEN

目标测试最终运行：

```text
npm test -- tests/main/app-window-manager.test.ts tests/main/app-package-uninstaller.test.ts tests/main/app-lifecycle-coordinator.test.ts
```

结果：`3 files passed`，`39 tests passed`。

受影响回归：

```text
npm test -- tests/main/app-runtime-manager.test.ts tests/main/app-registry-repository.test.ts tests/main/app-package-installer.test.ts tests/main/app-center-service.test.ts
```

结果：`4 files passed`，`29 tests passed`。

全量回归：

```text
npm test
```

结果：`48 files passed`，`258 tests passed`。

## Typecheck

命令：`npm run typecheck`

结果：未通过，但只剩需求中已知的 6 个 Task 3 所属 `enabled` 契约错误：

- `src/main/services/app-center-service.ts`：2 处（目录合并缺少 `enabled`）。
- `src/main/services/app-package-installer.ts`：1 处（安装记录缺少 `enabled`）。
- `tests/main/app-center-service.test.ts`：2 处（测试记录字面量缺少 `enabled`）。
- `tests/main/app-host-file-capability.test.ts`：1 处（测试记录字面量缺少 `enabled`）。

Task 2 新增和修改文件无 typecheck 错误；未越界修复上述 Task 3 消费者。

## 提交

代码与本报告提交 SHA：`c5ac59df6ec5a3259d541e68a7f4cef4f1dd95f8`。

提交信息：`feat(apps): coordinate application lifecycle and uninstall`

## 自审

- `closeApp` 只等待并收口目标 `appId` 的窗口，不影响其他应用；保存和销毁失败会继续处理其余目标并聚合中文错误。
- 协调器使用每应用独立 Promise 队列，队列尾部吞掉前一步拒绝，避免一次失败阻断后续操作；冷启动失败保留 `enabled` 并写入 `broken`/中文错误，手动启用失败回滚为 disabled。
- 停用和卸载均先持久化 disabled，再关闭窗口、停止 runtime；卸载成功后才删文件和移除注册记录，停止/删除失败保留 disabled 注册记录。
- 种子应用在队列和 runtime/文件副作用前拒绝卸载；卸载器只根据固定 `appsRoot` 与校验后的 `appId` 拼接目标路径，保留数据时仅删除 `data` 以外条目。
- 协调器通过最小 DI 接口消费解析器，`AppResolvedApp` 包含 `record`、`installPath`、`dataDirectory`、`manifest`，没有直接依赖 Electron。

## 关注点

- Task 3 仍需把 Electron IPC 的启动、RPC、安装后处理和卸载入口接入协调器，并补齐 6 个 `enabled` 契约错误。
- `AppRuntimeManager` 内部仍保留 `failed` 状态；按 Task 1 ruling，Task 3 对 disabled 应用向外映射为 `stopped`。

## 修复轮 1

### RED

新增四组回归用例后运行：

```text
npm test -- tests/main/app-window-manager.test.ts tests/main/app-lifecycle-coordinator.test.ts tests/main/app-package-uninstaller.test.ts
```

结果：`2 files failed`，`5 failed / 39 passed`。失败分别证明：`closeApp` 销毁失败会错误移除映射；停用和卸载在关窗失败后不会继续 stop；更新没有先关窗；更新关闭失败仍会继续启动新 runtime。

### GREEN

修复后运行同一命令：

```text
npm test -- tests/main/app-window-manager.test.ts tests/main/app-lifecycle-coordinator.test.ts tests/main/app-package-uninstaller.test.ts
```

结果：`3 files passed`，`44 tests passed`。

修复内容：`closeApp` 仅在 destroy 成功时清理 mappings，`closeAll` 仍在最终退出时强制清理；停用/卸载分别捕获 close 与 stop 并聚合中文错误；更新严格按 closeApp、stop、解析/start 顺序执行，前两步任一步失败都不启动新 runtime；失败后的 disabled 停用会再次尝试 close/stop。

修复轮 1 实现提交 SHA：`6854cd43137e623984ec5abe31a4129051b1d8b3`。
