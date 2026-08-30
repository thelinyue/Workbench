# Task 3 实施报告

## 范围

- `src/main/ipc.ts`：接入 `AppLifecycleCoordinator`、runtime adapter、种子初始化、启停/卸载 IPC、安装后处理和初始化等待。
- `src/main/services/app-center-service.ts`：输出 `AppCenterItem`，补齐 builtIn/runtimeState，disabled 强制映射 stopped。
- `src/main/services/app-package-installer.ts`：新安装默认 enabled，更新保留原 enabled。
- `src/preload/index.ts`、`src/shared/bridge.d.ts`：暴露并声明 setEnabled/uninstall 及 AppCenterItem。
- 相关主测试：补充桥接、状态映射、安装 enabled 保留；补齐一个 enabled 类型契约字面量。

## TDD 证据

### RED

先修改测试，再运行：

```text
npm test -- tests/main/app-center-bridge.test.ts tests/main/app-center-service.test.ts tests/main/app-package-installer.test.ts
```

结果：新增断言按预期失败。桥接缺少 set-enabled/uninstall 和协调器入口；应用中心项缺少 builtIn/runtimeState；安装器记录缺少 enabled。

### GREEN

实现后目标测试及 Host 能力回归：

```text
npm test -- tests/main/app-center-bridge.test.ts tests/main/app-center-service.test.ts tests/main/app-package-installer.test.ts tests/main/app-host-file-capability.test.ts
```

结果：`4 files passed`，`21 tests passed`。

### 修复轮 RED/GREEN

为验证新增的安装竞态测试能够捕获修复前行为，先临时将 `apps:install` mutation 为“在协调器队列外执行 `appCenter.install`，完成后再调用 `afterInstall`”，然后运行最小行为测试：

```text
npm test -- tests/main/app-launch-ipc.test.ts -t "安装下载未完成时不会让同一应用的停用抢先落库"
```

结果：测试在 `tests/main/app-launch-ipc.test.ts:153` 的 `expect(disableSettled).toBe(false)` 处失败，实际值为 `true`；这证明旧实现会在安装 gate 释放前让停用操作完成。该失败路径还因安装 gate 未释放导致清理 hook 超时，随后立即恢复正确实现，未保留 mutation。

恢复 `lifecycleCoordinator.install` 后重新运行同一命令，结果为 `1 file passed`、目标测试 `1 passed`（同文件其余 4 个测试因 `-t` 跳过）。恢复后的全量回归结果为 `48 files passed`、`271 tests passed`。

最终全量测试：

```text
npm test
```

结果：`48 files passed`，`271 tests passed`。

## Typecheck

命令：`npm run typecheck`

结果：通过，零错误。Task 2 报告中的 6 个 `enabled` 契约错误已全部清零。

## 提交

初始实现提交 SHA：`878948e`。

修复轮提交 SHA：`91e1f8987f0ba6b9174a58224ec7012a4052ca78`。

提交信息：`fix(apps): serialize install lifecycle`

## 自审

- launch/get-entry-url/invoke/reload、app-window context 和安装后启动均通过协调器的每应用队列；runtime adapter 显式把 `AppResolvedApp` 转换为 `{ appId, installPath, dataDirectory, manifest }`。
- runtime 事件和通知监听在 initialization Promise 启动 `startEnabledApps` 前注册；list/refresh 和退出清理等待同一 initialization Promise。
- disabled 应用保留 `errorMessage`，但 `runtimeState` 对外固定为 `stopped`；Host capability 也通过 `runEnabled`，不能绕过 disabled 检查。
- IPC 对 setEnabled/uninstall 使用 Zod 校验，主进程额外拒绝 analysis-center/terminal 卸载；builtIn 只配置这两个种子应用。
- 新安装 enabled=true，更新由安装器和应用中心共同保留旧 enabled；开发覆盖仅对已安装且 enabled 的应用开放。

## 关注点

- `RegisterWorkbenchIpcOptions.closeAppWindow` 已接入协调器但保持可选，以兼容当前 Task 3 测试和装配；Task 5 需要从 `AppWindowManager.closeApp` 注入真实实现，否则当前装配下停用/卸载没有原生窗口可收口。
- 运行时/目录资源缺失时现有测试会输出中文 stderr，但不影响测试结果。
- 安装本体与安装后的 enabled 判断、runtime 重启现在由 `lifecycleCoordinator.install` 放入同一 `appId` 队列，避免安装与停用/卸载交错；对应 IPC 静态契约已同步更新。
