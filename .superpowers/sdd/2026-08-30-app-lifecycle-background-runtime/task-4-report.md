# Task 4 实施报告

## 范围

- `src/renderer/src/App.tsx`
  - 应用中心改用 `AppCenterItem`，展示 `enabled` 和 `runtimeState`。
  - 已安装卡片增加语义化原生 checkbox 启用开关；启停、安装、更新、卸载期间使用 disabled 和 `aria-busy` 防止重复提交。
  - 非内置应用提供带 `title`/`aria-label` 的 `Trash2` 卸载按钮；内置应用仅显示“内置”。
  - 增加默认保留数据的卸载确认框；永久删除选项明确说明配置、历史记录和报告不可恢复。
  - 桌面初始化、应用库、打开应用、桌面布局同步统一使用 `activeVersion && enabled`；禁用应用保留在应用中心但打开按钮不可用。
  - 处理 `workbench:changed` 时关闭禁用或卸载应用的虚拟窗口，重新归一化并保存桌面布局；重新启用只恢复入口。
  - 分析中心拖入和通知快照仅在应用启用时访问运行时，禁用时提示先启用。
  - 为关键状态广播同步逻辑补充中文设计注释。
- `src/renderer/src/styles.css`
  - 增加启用状态、运行状态、内置标识、卸载图标按钮和卸载弹窗样式；控件保持稳定尺寸并兼容已有亮/暗主题。
- `tests/renderer/app-center.test.ts`、`tests/renderer/app-shell.test.ts`
  - 增加启停、运行态、卸载确认、入口过滤、广播同步和禁用拖入/通知路径的源码契约测试。
- `src/renderer/desktop-layout.ts`
  - 未修改；现有 helper 已能接收调用方过滤后的应用 ID。

## TDD 证据

### RED

先写新增渲染测试，再运行：

```text
npm test -- tests/renderer/app-center.test.ts tests/renderer/app-shell.test.ts
```

结果：`2 files failed`，`6 failed / 15 passed`。失败集中在缺少 checkbox、`setEnabled`、卸载调用/确认文案、`activeVersion && enabled` 过滤和广播同步实现，证明新增行为不是既有代码覆盖的。

### GREEN

实现后首次运行发现 2 个既有源码契约仍匹配旧签名/旧应用库过滤断言；同步更新测试契约后重新运行：

```text
npm test -- tests/renderer/app-center.test.ts tests/renderer/app-shell.test.ts
```

结果：`2 files passed`，`21 tests passed`。

清理因同步逻辑替代而产生的未使用刷新函数后再次验证：

```text
npm test -- tests/renderer/app-center.test.ts tests/renderer/app-shell.test.ts tests/renderer/desktop-layout.test.ts
```

结果：`3 files passed`，`33 tests passed`。

### 本轮审查回归

审查发现单值 `busyAppId` 在 A 操作期间可能被 B 操作覆盖，导致 A 结束时提前解锁；同时补充了暗色主题卸载按钮和卸载确认框键盘交互的回归约束。

先补充交错操作状态以及卸载对话框焦点/Escape 断言，再运行定向测试：

```text
npm test -- tests/renderer/app-center.test.ts
```

结果：新增断言在实现前按预期失败，分别暴露出操作状态模块缺失，以及取消按钮初始聚焦、非忙碌 Escape 关闭和监听清理尚未实现。

随后实现不可覆盖的 `beginAppOperation`/`completeAppOperation` 状态 helper，并补齐对话框焦点与监听生命周期。修复后同一命令结果为：`1 file passed`，`14 tests passed`。

### 第二轮审查回归

审查发现初始聚焦与 Escape 监听共用 `[busy, onCancel]` effect；父组件内联 `onCancel` 或删除数据状态变化会重复执行聚焦，抢回用户当前焦点。

此前为快速覆盖该问题曾抽取 `uninstall-dialog-state` 纯 helper 并添加布尔行为测试，但该测试只能验证取反逻辑，无法验证 React effect 或真实 DOM 聚焦。本轮代码质量回修已删除该一次性 helper、import 和伪行为测试。

生产代码保留最小实现：`UninstallDialog` 使用 `useRef` 记录是否完成首次聚焦，直接在依赖为 `[]` 的 effect 中判定；Escape 监听继续独立使用 `[busy, onCancel]` 读取最新状态并清理监听。本轮没有新增行为，也不伪造 RED 测试。

## 验证结果

```text
npm test -- tests/renderer
```

结果：`16 files passed`，`91 tests passed`。

```text
npm run typecheck
```

结果：通过，零错误。

```text
npm test
```

结果：`48 files passed`，`279 tests passed`。既有测试会输出少量内置核心应用资源缺失的中文 stderr，这是测试环境跳过种子安装的既有提示，不影响结果。

`git diff --check` 无实际错误。

## 提交

- 实现与测试提交 SHA：`3446f9b3617fe9445511be6bcb07ef18191679b5`
- 初始实现提交信息：`feat(renderer): sync app lifecycle controls`
- 本轮审查修复与回归测试提交 SHA：`08a003034534886a8a13a7f0be3a4fa3e2168c55`
- 本轮提交信息：`fix(renderer): harden app center operation locking`
- 第二轮审查修复与回归测试提交 SHA：`8cd8aae876aac36c20651957c6c4ecd701089923`
- 第二轮提交信息：`fix(renderer): keep uninstall dialog focus stable`
- 第三轮代码质量回修提交 SHA：`a8ad20bc53e44685b2384d07c15efc2528e9bdca`
- 第三轮提交信息：`refactor(renderer): remove focus test abstraction`
- 本报告随后作为独立文档提交，提交号以报告提交后的 `git rev-parse HEAD` 为准。

## 自审

- 所有渲染入口的应用可用性判断均要求有效版本且 enabled；应用中心仍展示 disabled 卡片，且不会因重新启用自动创建窗口。
- 状态广播同步使用最新 `apps:list` 结果关闭虚拟窗口，并在布局实际需要变更时保存归一化结果；已保存的可用应用位置继续优先保留。
- 启停和卸载失败会保留应用中心当前状态和卸载对话框，按钮在请求完成前不可重复触发。
- 卸载默认 `deleteData=false`，内置应用没有卸载操作；删除数据复选项使用明确的破坏性中文文案。
- 卸载确认框打开后将焦点放到“取消”；Escape 仅在非忙碌时关闭，忙碌期间保留确认框，并在组件卸载时清理全局键盘监听。
- 初始聚焦只在对话框挂载时执行一次；busy/onCancel 更新只刷新 Escape 监听，不会抢回用户当前焦点。
- 未新增依赖，图标均来自现有 `lucide-react`。

## 关注点

- 当前 renderer 测试基础设施使用 Node 环境下的源码契约测试，没有 DOM 级 Electron 交互测试；真实 IPC 生命周期由主进程测试覆盖，控件结构和关键字符串由本任务测试覆盖。
- 因此焦点转移和 Escape 的实际浏览器事件分发仍未由 DOM 测试覆盖，当前回归测试只约束源码中的 ref、条件和监听清理，未声称验证真实 React effect 或 DOM 聚焦。
- `workbench:changed` 期间会额外读取一次应用目录以获得原子状态快照；这是关闭窗口和归一化布局所需的同步边界。
