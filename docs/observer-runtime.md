# Observer、记录与回放运行时（M4）

日期：2026-10-03。契约见 [observer-protocol.md](observer-protocol.md) 与 [replay-format.md](replay-format.md)；本文只描述 Runtime 侧的实现与不变量。终局渲染属 M5。

## 单局数据流

```text
Agent 响应 → 解析/ID 校验 → Game.Step（Core 唯一所有者）
           → ObserverProjection.Apply → ObserverPatch
           → RunCommitter：Hub.Prepare → 权威 append 成功 → Hub.Publish
           → 订阅者队列（各自有界）→ Viewer
```

`RunCommitter` 是唯一发布者。`Hub.Prepare` 在不改变当前快照的前提下预先完成整份 reduction（把 envelope 冻结、克隆、应用到一份 `VisualState` 副本），`Publish` 再校验 `PreviousSeq == 当前 BaseSeq` 才提交，因此任何非所有者或过期发布都抛错而不会推进快照。

记录失败时该步不进入 `Committed`：`GameRunner` 捕获 `RecordingException`，摘要按最后已提交快照报告 tick/哈希，并把 `error.code=record_error` 写入摘要；记录文件因缺少 footer 被 `replay`/`verify` 判为 `incomplete`。普通缓冲写入成功不等于断电持久。

## 订阅与背压

- `Register(capacity = 128)` 在同一把锁内返回 `(SnapshotMessage, ObserverSubscription)`，快照 `BaseSeq` 表示其已涵盖的发布序号，其后第一条增量必须是 `BaseSeq + 1`。
- 每个订阅者一条 `Channel.CreateBounded` 队列（`FullMode = Wait`，单写者）。发布者对每个订阅者 `TryWrite`；写不进去就置 `RequiresResync`、摘除订阅并以其异常完成通道，**不会** DropOldest 后继续应用状态。
- 硬上限与期限：单次 `Register` 的容量必须在 1–1024（默认 128，越界抛 `ArgumentOutOfRangeException`）；同一 Hub 最多 32 个订阅，第 33 个注册抛 `InvalidOperationException`。权威记录每次 append 有 5 秒期限，超时即闩锁（此后不再写 footer）；释放写入器与读取器的等待上限为 2 秒。
- `ConsoleObserverSink` 消费时先写初始快照，再逐条写增量；每次写有 1 秒期限，超时记 `output_timeout`，`RequiresResync` 记 `resync_required`。**这两类诊断只作为 stderr 的一行 `observer_error` 输出，不进入 `run/1` 摘要**——需要判断观察者是否脱落时读取 stderr，而不是摘要。
- 因此慢观察者只会脱离或丢帧，规则结果（tick、kind、逐步 `core_hash`）与无订阅者时完全一致；M4 CLI 黑盒检查通过“故意不读 stdout 管道”验证了这一点。

`seq` 是发布顺序，`tick` 是规则回合；`agent_status`（waiting/action_received/stopped/errored）可以增加 `seq` 而不推进 `tick`。

## 回放与核验

`replay` 只读记录文件：先输出初始 `snapshot`，再按记录顺序输出每条 `status_record.observer_status` 与 `step_record.observer_batch`，摘要写到 stderr：`status`（`completed`/`incomplete`）、`last_tick`、`last_seq` 与 `lines`（读取器最后停留的记录行号；统计的是文件记录数，含 header 与 footer，因此完整 N 步记录为 `2N+3`，而 stdout 只导出 `2N+2` 条 envelope）。它不构造 `Game`、不启动 Agent，也不读场景文件——因此可以在没有审计对象或解释器的环境里播放。

`verify` 用记录内的初始场景与 `core-state/1` 重演：逐步比对 `core_hash`、动作反馈、领域事件与 Observer 补丁；首个不一致时返回该步的记录行号（`line`）与该步产生的 `tick`，以及原因（`core_hash_mismatch`、`outcome_mismatch`、`events_mismatch`、`observer_patch_mismatch`、`initial_snapshot_mismatch`、`step_after_episode`）。原因不止这些：解析与序号类问题还会返回 `invalid_record`、`line_too_long`、`sequence_gap`、`status_tick_mismatch`、`data_after_footer` 等，同样带 `line`。规则/编码版本不符返回 `unsupported_version` 而不假装核验过。结果字段为 `valid`、`status`、`error`、`line`、`tick`、`core_hash`（没有单独的 `verification` 字段）。

缺失 footer 或末行残缺：`replay` 仍导出已提交前缀，但退出码非零且摘要 `status=incomplete`；`verify` 返回 `valid=false`、`status=incomplete`、`error=incomplete`。中间行损坏（非法 JSON、超长记录、footer 之后仍有数据）直接停止并给出 `line`。

## 命令行与退出码

```powershell
dotnet run --project src/AgentGame.Cli -c Release -- scenario generate --seed 42 --out artifacts/facility42.json
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario artifacts/facility42.json --headless --record artifacts/run.jsonl -- python -u agents/random_agent.py --seed 42
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario artifacts/facility42.json --observer-stdout --record artifacts/run.jsonl -- python -u agents/explorer_agent.py
dotnet run --project src/AgentGame.Cli -c Release -- replay artifacts/run.jsonl
dotnet run --project src/AgentGame.Cli -c Release -- verify artifacts/run.jsonl
```

- `--record <new-jsonl>` 只创建新文件，已存在即失败（`FileMode.CreateNew`）；未指定时不会自动保存完整流。
- `--observer-stdout` 独占 stdout，运行摘要改走 stderr；`--headless` 表示不挂任何终端旁观者，与 `--observer-stdout` 互斥（冲突按用法错误返回 2）。
- 退出码：0 正常结束（含规则失败/回合截断）、1 运行失败或 `verify`/`replay` 判定记录不可用、2 参数错误、130 用户取消。原因细分以 JSON 字段（`kind`、`error.code`、`valid`、`status`、`line`）为准，脚本应据此分支而不是猜退出码。

## 所有权与不变式（便于回归检查）

1. 同一局只有一个逻辑所有者调用 `Game`；订阅、stderr 排空和记录写入都不访问可变 Core 状态。
2. 权威记录成功先于发布；失败闩锁后不再写入、不再发布。
3. 快照 `BaseSeq` 与后续增量序号严格连续，`BaseSeq + 1` 是第一条增量。
4. 订阅队列有界；拥塞表现为脱离/重同步，绝不表现为“继续应用但状态错误”。
5. `replay` 不依赖 Core/Agent；`verify` 必须用同版规则与编码，否则明确拒绝核验。
6. 外发 DTO 在边界处被重新解析/克隆，调用方持有的可变数组不会渗透进运行状态。

## 多玩家 Observer 库（M7.2b）

`MultiObserverSession` 从同一不可变多人快照和每席 `MultiObservation` 建立 `agent:0..N-1` 与 `spectator`。席位只看自己的观测与历史；其他席位动作只向它发布公开阶段/终局事件。旁观视图显示公开全图及各席位实体，绝不作为席位输入。旁观库存表示队伍已获取的物品；席位库存仅有共享门卡与本席携带的核心。

各视图 `GetHub(view).Register()` 原子返回 v2 快照与订阅；使用 `MultiVisualState` 归并。状态消息可以只发布到一个视图，所以视图的 seq 可以不同；同一 Core 步更新所有视图的 tick。

提交顺序接口为 `PrepareStep(after, observations, step)` → 外部权威记录准备结果的 `Batches` → `Commit(prepared)`。准备阶段克隆缓存，不改变任何已提交快照；记录失败时丢弃准备结果并结束运行，不能继续接受动作。`PrepareStatus(view,status)` 使用相同边界。读到的 `Batches` 及订阅消息都有独立数组；旧提交、重复提交及跨会话提交均被拒绝。

本阶段只实现可测试的 Runtime 组件；多人调度、权威 v2 文件 I/O、CLI 和终端接线属于 M7.3–M7.4。

后续 M7.3 已接入双席位运行器及 v2 权威文件 I/O；CLI 与终端接线仍待 M7.4，详见 [多人 Runtime](multiplayer-runtime.md)。

运行调用方必须由同一逻辑所有者串行执行准备、记录和提交。提交令牌与边界检查可拒绝过期或重复结果，但不负责把多个调用方的外部文件 I/O 串行化；读取快照、注册和消费订阅可以并发进行。
