# 终端视图（M5，纯渲染）

实现：`src/AgentGame.Cli/TerminalView.cs`（`internal sealed class TerminalView`）。零第三方依赖，只用基础库与 ANSI 转义序列；只引用 Protocol 与 Runtime，不引用 Core，不计算任何游戏规则。

逻辑状态由既有 reducer `VisualState` 拥有（只应用显式 patch）；本组件只把该状态变成文本，并把渲染状态与数据状态分开持有。

## API

| 成员 | 语义 |
|---|---|
| `TerminalView(Stream/TextWriter, int? width, int? height, bool useColor, Func<TimeSpan>? clock)` | 输出流（UTF-8 无 BOM、`leaveOpen`）或调用方自有的 writer；`width/height` 缺省 80×24；`clock` 只用于 waiting 计时，不注入则渲染是状态的纯函数 |
| `void RenderSnapshot(SnapshotMessage)` | 建立/重置显示基线；快照非法时抛 `ProtocolException`，旧基线保持不变 |
| `bool TryApply(object)` | 按 seq 应用 `step_batch` / `agent_status`；拒绝时返回 false |
| `string RenderFrame()` | 本帧完整文本，不追加多余空行；不修改显示状态 |
| `void Draw()` | 写一帧到输出并 flush（纯文本模式每帧补一个 LF） |
| `void Reset()` | 清空基线、显示状态与动画/计时缓存；此后必须先取新快照 |
| `string RenderRestoreSequence()` | 退出时恢复终端（重置属性、显示光标）；`useColor=false` 时为空串 |
| `Width` / `Height` | 布局尺寸，分别夹到 `[1,4096]` 与 `[1,512]`；可变尺寸按传入值重新布局 |
| `HasSnapshot` / `NeedsResync` / `LastRejectReason` | 供调用方判断是否需要重新取快照与诊断路由 |

组件自身不读写 stdout/stderr/Console、不读文件、环境变量或凭据；`useColor=false` 时输出不含任何 ESC 字节。

## 序号连续性与重同步

- 快照覆盖 `base_seq`，其后第一条必须是 `base_seq + 1`；组件在调用 reducer **之前**自行校验。
- 缺口、重复/过期 seq、run_id/view/protocol 变化、tick 与消息种类不符、patch 被 reducer 拒绝：`TryApply` 返回 false，`NeedsResync=true`，**显示状态原样保留**（继续显示最后一帧可信世界，表头前置 `!resync(原因)`），此后所有 envelope 一律拒绝，直到 `RenderSnapshot` 重新建立基线。不连续的 patch 绝不补进错误世界，也不与后续 patch 混合。
- 传入非 `step_batch`/`agent_status` 对象只返回 false（`unsupported_envelope`），不使基线失效。

## 记忆与当前可见

- `last_seen_tick == null` 是唯一的“当前可见”依据；非 null 只是显示记忆，永不当作当前真实状态。
- 记忆字形表（颜色模式下同时用暗灰）：`.`→`,`、`#`→`%`、`k`→`K`、`C`→`c`、`+`→`=`、`/`→`\`、`>`→`v`；玩家 `@` 始终是当前位置。
- 没有任何 tile 记录的格子渲染为 `?`，不得显示成 floor/wall；记忆格的颜色/字形都与当前格不同，`useColor=false` 时仍可区分。
- 可见性只从消息读取，绝不按半径或几何重新推导。

## 布局

- 宽度 ≥ 40：状态行（tick、阶段、agent 状态、库存、seq、run）+ 上次动作反馈行 + 带边框地图（以玩家为中心，夹在记忆范围内；盒宽铺满可用宽度）+ 事件区（最近若干条语义事件，最旧在上）。
- 宽度 < 40：紧凑摘要（t/阶段、agent、库存、上次反馈）；宽度 ≥ 20 时再显示玩家附近的无边框小窗口。
- 宽度 < 20：仅摘要。所有行都裁剪到给定宽度，任意尺寸（含 1 列）都不抛异常、不越界。
- 颜色模式帧以隐藏光标 + 光标归位开头，逐行 `ESC[K`、末行 `ESC[J`，缩短的帧不会残留旧内容。

## 接线（M5 已完成）

- `run --tui`：`TerminalObserverSink` 注册订阅 → `RenderSnapshot(registration.Snapshot)` → 逐条 `TryApply` → `Draw()`，并用 `ReplayControls` 轮询按键（空格暂停、`.`/→ 单步、`+`/`-` 倍率、`q` 退出）。`TryApply` 返回 false 且 `NeedsResync` 时 sink 记 `resync_required` 并停止渲染（画面保留最后一帧可信世界），诊断走 stderr 的 `observer_error`。
- `play`：默认在交互终端上启用同一渲染路径；`--plain` 或重定向时只输出按键提示（写 stderr），不绘制帧。
- `--tui` 与 `--headless`、`--observer-stdout` 互斥；`--tui` 在输出被重定向时按用法错误拒绝（退出码 2），因为 ANSI 帧会污染机器输出。
- 重定向时**不会**创建终端 sink：`--tui` 在输出被重定向时按用法错误拒绝（退出码 2），`play` 在非交互时走 `--plain` 提示路径。因此不存在“重定向渲染”分支，也不会把 ANSI 写进机器输出。
- `play` 以 `interactiveControls: false` 创建 sink：人类输入由 `HumanInput` 独占控制台，sink 只渲染，避免两个线程争抢同一个 `Console.ReadKey`。暂停/单步/倍率只属于 `run --tui`。
- `ReplayControls` 的轮询在输入不可用时自动退化为空操作。
