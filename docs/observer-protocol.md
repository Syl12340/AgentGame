# Observer 协议（observer/1）

M0 冻结的消息契约。Protocol 已实现 DTO/编码和 fixtures；投影、reducer、Hub、原子注册与重同步已在 M4 实现并随 M4 验收通过，实现说明见 [observer-runtime.md](observer-runtime.md)。

## 信息与显示记忆

Agent 输入仅含本次可见信息。Observer 的 `view=agent` 可以保留显示记忆：当前可见格的 `last_seen_tick=null`；历史格保留最后已知值与最后看到的 tick，不能当成当前真实状态。Agent 不接收这份记忆。以后全图调试视图必须显式指定并与 Agent 隔离。

## 消息

| 类型 | 内容 |
|---|---|
| snapshot | protocol、run_id、view、base_seq、tick、agent_status、完整 state |
| step_batch | protocol、run_id、view、seq、tick、agent_status、events、显式 patch |
| agent_status | protocol、run_id、view、seq、tick、agent_status |

Snapshot 的 `state` 包含 visible_radius、tiles、entities、inventory、mission_phase、episode。每个完整 tile 包含 x/y、terrain、item、is_exit、door_open、last_seen_tick；nullable 字段显式输出 null。当前可见集合可以由 last_seen_tick=null 的 tiles 得到。

StepBatch 的 `patch` 固定包含以下全部字段，M4 读取器须拒绝缺失字段，不能用 C# 默认值悄悄补齐：

- `tile_upserts`：完整 tile 的新增/替换（按坐标），包含清空值。item/door_open/last_seen_tick 的 null 清空旧值，不能省略后继承旧物品。
- `entity_upserts`：完整实体新增/替换（按 id）；`entity_removals` 删除列出的实体。id 稳定，位置整数。更新集合与删除集合不得对同一 id 冲突，tile 坐标不得重复。
- `visible_tiles`：当前完整可见坐标集合，全量替换；空集合也必须明确给出。
- `inventory`、`mission_phase`、`episode`：全部是本步最终值，全量替换。库存可为空，阶段不可为 null，未终局的 episode 必须显式为 null。

空的 upsert/removal 数组表示该集合没有变化。实体与 tile 的记录被整体替换，非“逐个属性若缺失就继承”。Viewer 动画从 events 得到语义提示；逻辑状态由 patch 得到，不重新计算游戏规则。协议 DTO 的数组是序列化载体，M4 的发布者必须复制/冻结其所有权，不能把可变共享数组发布给多个订阅者。

## 顺序、状态与重同步

`seq` 是本次 run/view 的发布顺序，`tick` 是 Core 回合；status 可以增 seq 而不增 tick。seq/base_seq/tick 非负且不超过 JSON 安全整数 2^53−1，M4 边界校验落实。`base_seq` 是快照已经涵盖的最大发布号，Snapshot 不额外占号；其后第一条必须为 base_seq+1。

Runtime 在同一所有者边界注册并返回 `(snapshot, subscription)`；新 run_id/view 或队列拥塞后重新开始快照。重同步清空旧逻辑/动画状态。每个订阅独立有界队列；慢订阅脱离，不 DropOldest 后继续应用错误状态。

agent_status 为 waiting/action_received/stopped/resync_required/errored。waiting 表示 Host 正在等待响应，不代表知道模型内部推理。elapsed、动画和呼吸灯由 Viewer 单调时钟计算，不周期性发布 pulse。

M4 必须验证：旧快照+后续批次等于当前重新投影；新订阅不遗漏首条、不重复应用快照覆盖条。格式正确不等于已证明这些流转性质。

## 示例

```json
{"type":"snapshot","protocol":"observer/1","run_id":"demo","view":"agent","base_seq":0,"tick":0,"agent_status":"waiting","state":{"visible_radius":3,"tiles":[],"entities":[],"inventory":[],"mission_phase":"find_key","episode":null}}
{"type":"agent_status","protocol":"observer/1","run_id":"demo","view":"agent","seq":1,"tick":0,"agent_status":"waiting"}
{"type":"step_batch","protocol":"observer/1","run_id":"demo","view":"agent","seq":2,"tick":1,"agent_status":"action_received","events":[{"type":"waited"}],"patch":{"tile_upserts":[],"entity_upserts":[],"entity_removals":[],"visible_tiles":[],"inventory":[],"mission_phase":"find_key","episode":null}}
```

示例展示字段与序号，具体格数据看 `tests/Fixtures/Protocol/observer/` 和回放 fixture；生产投影（`ObserverProjection`/`VisualState`）与订阅 Hub 已在 M4 交付并通过验收。

## 消息大小上限

Observer 单条消息的 UTF-8 内容上限为 4 MiB（4,194,304 字节），不计可选的单个 LF 或 CRLF 行结束符。该上限容纳 128×128 地图的完整显示记忆快照；Agent 协议仍独立使用 64 KiB 上限。读取与编码均按实际 UTF-8 字节校验，JSON 嵌套深度上限为 32。
