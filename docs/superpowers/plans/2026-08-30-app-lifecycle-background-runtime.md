# 应用生命周期与可靠后台运行实施计划

> **执行约束：** 每个任务由新的 `gpt-5.6-luna`、`xhigh` 实现子进程完成；子进程不得继续委派。主进程逐任务审查规格、代码和测试证据。

**目标：** 让已启用应用在 Workbench 启动后可靠后台运行，支持即时启停、受保护卸载、准确 runtime 状态和托盘驻留。

**架构：** `AppRuntimeManager` 提供带 ready 握手的真实运行状态；`AppLifecycleCoordinator` 按 appId 串行编排注册表、runtime、窗口、安装更新与卸载；Electron 生命周期控制器负责主窗口隐藏、托盘恢复和显式退出。应用 manifest、Catalog 与 Host API 版本保持不变。

**技术栈：** Electron 35、TypeScript、React 19、SQLite、Vitest、Node Worker Threads。

## 全局约束

- 严格 TDD：先写失败测试并记录 RED，再写最小实现并记录 GREEN。
- 只修改本计划直接要求的代码；不做相邻重构，不新增依赖、hash、baseline、gate 或正式发布操作。
- 新增关键类、状态机和复杂生命周期顺序必须有完整中文注释；部署者可见错误和日志使用中文。
- 所有旧安装和新安装默认启用；未安装目录项的 enabled 固定为 false。
- `analysis-center` 与 `terminal` 是唯一不可卸载的种子应用。
- Worker 启动超时固定为 10_000ms；runtime 状态为 stopped、starting、running、stopping、failed。
- runtime running 只代表 backend/RPC 就绪；分析中心目录健康继续由 monitor.status 表达。
- 关闭主窗口只隐藏到托盘；不增加开机自启。

---

### Task 1: 注册表启用状态与可靠 Runtime 状态机

**文件：**
- 修改：`src/shared/app-contract.ts`
- 修改：`src/main/data/app-registry-repository.ts`
- 修改：`src/main/services/app-runtime-manager.ts`
- 修改：`src/main/services/app-backend-worker.ts`
- 测试：`tests/main/app-registry-repository.test.ts`
- 测试：`tests/main/app-runtime-manager.test.ts`

**产出接口：**
- `AppInstallRecord.enabled: boolean`
- `type AppRuntimeState = 'stopped' | 'starting' | 'running' | 'stopping' | 'failed'`
- `AppRuntimeManager.getState(appId: string): AppRuntimeState`
- `AppRuntimeManager.start(...)` 等待 Worker `{ type: 'ready' }`，默认超时 10_000ms

**行为：**
- 新表包含 `enabled INTEGER NOT NULL DEFAULT 1`；旧表通过 `PRAGMA table_info(installed_apps)` 检测并执行一次 ALTER TABLE。
- 仓储提供 `setEnabled(id, enabled)` 和 `remove(id)`；读写布尔值严格映射 0/1。
- Worker 在 backend 工厂完成且 RPC session 已注册后发送 ready。
- 同 appId 并发 start 共享 Promise；启动异常、提前退出和超时进入 failed，超时强制 terminate。
- invoke 仅允许 running；stop 进入 stopping 并保留既有 5 秒清理协议；纯 Web runtime 直接进入 running。

**验证：**
- 先运行两个目标测试文件确认新增用例失败，再实现并运行至通过。
- 再运行 `npm run typecheck`。

### Task 2: 应用窗口收口、卸载文件策略与生命周期协调器

**文件：**
- 新建：`src/main/services/app-lifecycle-coordinator.ts`
- 新建：`src/main/services/app-package-uninstaller.ts`
- 修改：`src/main/services/app-window-manager.ts`
- 测试：`tests/main/app-lifecycle-coordinator.test.ts`
- 测试：`tests/main/app-package-uninstaller.test.ts`
- 修改测试：`tests/main/app-window-manager.test.ts`

**消费接口：** Task 1 的 enabled、runtime state、start/stop/getState。

**产出接口：**
- `AppWindowManager.closeApp(appId: string): Promise<void>`
- `AppLifecycleCoordinator.startEnabledApps(): Promise<void>`
- `AppLifecycleCoordinator.setEnabled(appId: string, enabled: boolean): Promise<AppInstallRecord>`
- `AppLifecycleCoordinator.runEnabled<T>(appId: string, operation: (resolvedApp) => Promise<T>): Promise<T>`
- `AppLifecycleCoordinator.afterInstall(appId: string, wasUpdate: boolean): Promise<void>`
- `AppLifecycleCoordinator.uninstall(appId: string, deleteData: boolean): Promise<void>`

**行为：**
- 协调器用每 appId Promise 队列串行化冷启动、启动、启停、更新、卸载；不同应用可以并行。
- 手动启用失败恢复 disabled；冷启动失败保留 enabled 并写入 broken/中文错误；单个失败不阻断其他应用。
- 停用先落库，再关闭目标窗口并停止 runtime；普通启动和 RPC 对 disabled 返回中文错误。
- closeApp 等待正在创建的目标窗口结算后保存状态并销毁，不影响其他应用。
- 卸载器只接受已校验 appId 和 appsRoot；保留数据时删除 appRoot 下除 data 外的全部条目，删除数据时删除整个 appRoot。
- 种子应用在任何文件或 runtime 副作用前拒绝卸载；停止或删除失败时保留注册记录且保持 disabled。

**验证：** 每组用例先 RED 后 GREEN，随后运行三个目标测试文件和 `npm run typecheck`。

### Task 3: IPC、安装更新、自动启动与共享桥接

**文件：**
- 修改：`src/main/ipc.ts`
- 修改：`src/main/services/app-center-service.ts`
- 修改：`src/main/services/app-package-installer.ts`
- 修改：`src/preload/index.ts`
- 修改：`src/shared/bridge.d.ts`
- 测试：`tests/main/app-center-bridge.test.ts`
- 修改测试：`tests/main/app-center-service.test.ts`、`tests/main/app-package-installer.test.ts`、`tests/main/app-launch-ipc.test.ts`、`tests/main/workbench-ipc-shutdown.test.ts`

**公共接口：**
- `interface AppCenterItem extends AppInstallRecord { builtIn: boolean; runtimeState: AppRuntimeState }`
- `apps.setEnabled(appId, enabled): Promise<AppCenterItem>`
- `apps.uninstall(appId, deleteData): Promise<void>`

**行为：**
- apps:list/refresh 为所有项附加 builtIn 和当前 runtimeState；未安装项 enabled=false、runtimeState=stopped。
- 安装新应用默认 enabled=true；更新读取并保留旧 enabled。
- runtime 事件和通知监听必须先注册，再在 seed 安装完成后调用 startEnabledApps；初始化 Promise 供列表请求与退出清理共同等待。
- launch/getEntryUrl/invoke/reload 和安装后的重启全部通过协调器，避免与停用/卸载竞态。
- IPC 使用 zod 校验 `{ appId, enabled }` 和 `{ appId, deleteData }`，主进程再次禁止卸载种子应用。
- 开发覆盖仅在应用已安装且 enabled 时开放。

**验证：** 先让桥接/安装/启动/退出用例失败，再实现；运行所有 `tests/main/app-*` 相关测试与 `npm run typecheck`。

### Task 4: 应用中心启停、卸载确认与桌面入口同步

**文件：**
- 修改：`src/renderer/src/App.tsx`
- 修改：`src/renderer/src/styles.css`
- 修改：`src/renderer/desktop-layout.ts`（仅在现有 helper 无法表达启用过滤时修改）
- 修改测试：`tests/renderer/app-center.test.ts`、`tests/renderer/app-shell.test.ts`、`tests/renderer/desktop-layout.test.ts`

**行为：**
- 已安装卡片显示稳定尺寸的启用开关和 runtime 状态文案；操作期间禁用重复提交。
- disabled 时禁用打开，仍显示卡片；桌面与应用库仅显示 activeVersion 且 enabled 的应用。
- 非内置应用显示 Trash2 图标卸载按钮和 tooltip；内置应用显示“内置”且没有卸载操作。
- 卸载确认框默认 deleteData=false；勾选项明确说明配置、历史记录和报告会永久删除。
- 状态广播后关闭已 disabled/卸载应用的虚拟窗口，重新 normalize 并保存桌面布局；重新启用后恢复入口。
- 分析中心拖入和通知快照检查 enabled，disabled 时提示先启用，不自动修改状态。

**验证：** 先扩展 renderer 测试并观察失败，再实现；运行 renderer 全量测试、`npm run typecheck`。

### Task 5: 托盘驻留、退出清理、打包资源与文档

**文件：**
- 新建：`src/main/services/workbench-tray-controller.ts`
- 修改：`src/main/services/workbench-lifecycle.ts`
- 修改：`src/main/index.ts`
- 修改：`package.json`
- 修改：`docs/module_map.md`
- 新建测试：`tests/main/workbench-tray-controller.test.ts`
- 修改测试：`tests/main/workbench-lifecycle.test.ts`

**行为：**
- 主窗口普通 close preventDefault 后 hide；second-instance、activate、托盘 click 和“打开工作台”统一 restore/show/focus。
- window-all-closed 不退出托盘驻留进程；托盘“退出”调用 app.quit，继续使用现有 before-quit 异步 gate。
- 最终清理等待 runtime 后销毁主窗口、应用窗口、托盘、协议和仓储；并发退出仍共享 Promise。
- packaged 从 `process.resourcesPath/tray/app-icon.ico` 读取，开发环境从 `assets/app-icon.ico` 读取；package extraResources 明确复制资源。
- 托盘 controller、关闭转驻留与最终退出顺序使用中文设计注释；错误日志可读。

**验证：** 先写 lifecycle/tray 失败用例，再实现；运行目标测试、全量 `npm test`、`npm run typecheck`、`npm run build`，最后运行 Workbench-Apps 的 `npm run test:analysis-center` 与 `npm run typecheck:analysis-center`。
