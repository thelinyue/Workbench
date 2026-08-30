# Task 1 实施报告

## 改动摘要

- 在 `AppInstallRecord` 中增加必填 `enabled: boolean`，并导出 `AppRuntimeState` 状态联合类型。
- 注册表新建表包含 `enabled INTEGER NOT NULL DEFAULT 1`；旧表通过 `PRAGMA table_info(installed_apps)` 检测后执行一次迁移；读写统一映射严格的 0/1，并增加 `setEnabled`、`remove`。
- Runtime Manager 增加 `stopped/starting/running/stopping/failed` 状态查询；backend Worker 必须发送 `ready` 后启动 Promise 才完成；同 appId 并发 start 共享 Promise；启动异常、提前退出和超时进入 failed，超时强制 terminate；invoke 仅接受 running；停止握手继续使用 5 秒清理协议。
- Worker 在 backend 工厂完成且 RPC session 注册后发送 `{ type: 'ready' }`。
- 测试覆盖旧表迁移、默认启用、布尔映射、ready 门控、并发 start、启动异常/提前退出/超时和状态流转。

## RED

命令：

```text
npm test -- tests/main/app-registry-repository.test.ts tests/main/app-runtime-manager.test.ts
```

结果：`2 files failed`，`9 failed / 11 passed`。新增行为按预期失败：`getState` 尚未提供、并发 start 未共享 Promise、启动异常未拒绝，仓储尚未返回/写入 enabled。首次测试中 PRAGMA 检查连接未关闭并产生 EBUSY 清理噪音，已在 GREEN 前修正测试资源释放后重新验证。

## GREEN

命令：

```text
npm test -- tests/main/app-registry-repository.test.ts tests/main/app-runtime-manager.test.ts
```

结果：`2 files passed`，`20 tests passed`。

额外全量回归：`npm test` 结果为 `46 files passed`、`240 tests passed`。

## Typecheck

命令：`npm run typecheck`

结果：未通过，剩余 6 个错误均来自 Task 3 清单外消费者尚未补齐必填 `enabled`：`src/main/services/app-center-service.ts`、`src/main/services/app-package-installer.ts` 及既有测试中的安装记录字面量。Task 1 修改范围内无 typecheck 错误；不将契约降为可选，以遵守 brief 的 `AppInstallRecord.enabled: boolean` 固定接口。

## 提交

实现提交 SHA：`56c74f0`

提交信息：`feat(runtime): add app lifecycle state tracking`

## 自审与遗留关注

- 自审确认仅修改 brief 指定的 6 个实现/测试文件；`git diff --check` 通过。
- Worker 事件处理带 RuntimeRecord 身份校验，避免超时 terminate 后旧 Worker 事件误伤同 appId 的重试实例。
- 后续 Task 3 需要在新安装、目录合并及既有测试构造的 `AppInstallRecord` 中补充 `enabled`，完成全局 typecheck；该改动不属于 Task 1 范围。
