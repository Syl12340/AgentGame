# Agent 协议（agent/1）

M0 冻结。UTF-8 JSONL；每条消息一行 JSON，发送后 flush；一局一个持久 Agent 进程，同一时刻只允许一个未完成请求。`request_id` 为字符串。协议与规则版本相互独立（`agent/1` 不代表规则等价）。本规范不依赖 Core，不计算游戏规则。

## 生命周期

| 阶段 | Host → Agent | Agent → Host |
|---|---|---|
| 启动 | `hello`：版本、动作规格 | `ready`：支持版本、名称 |
| 决策 | `observation`：request_id、tick、局部状态 | `action`：相同 request_id、单个动作 |
| 结束 | `episode_end`：结果与最后观测 | 正常退出或等待宿主关闭输入 |

## Host → Agent（Agent 读取）

`hello`：动作规格固定为 `["move","pickup","interact","wait"]`。

`observation` 不含历史、全图、seed、哈希或参考解；仅发送当前可见格。`last_result` 为上次动作反馈（未动作时省略）；`episode` 结束时给出（否则省略）。

`episode_end`：`result.kind` 为 `success` / `turn_limit`，`observation` 为最后观测。

## Agent → Host：严格解析（ready / action）

`AgentResponseParser` 只接受单个、顶层为对象、字段 snake_case 的 JSON 行。以下任一情形**拒绝**（`ProtocolException`）：

- 未知、缺失、多余、重复字段；字段 JSON 类型不符（如 `type` 为数字）。
- `type` 值不是 `ready`/`action`；`protocol` 不是 `agent/1`；`direction` 值不是 `north/east/south/west`（且区分大小写）。
- `action.type` 不是 `move/pickup/interact/wait`；`move`/`interact` 缺 `direction`；`pickup`/`wait` 带 `direction`。
- 任一出现字段为 `null`（`request_id`、`direction`、`action` 等）；`request_id` 或 `name` 为空字符串。
- 消息内容不是单个合法 JSON 对象：Markdown 围栏包住整行、自然语言、对象后的额外 JSON 或多条物理行。
- 无效 UTF-8、UTF-8 BOM；单行内容超过 64 KiB；JSON 深度超过 32。

**判定只按 JSON 形状进行**，不扫描字符串值里的子串或空白：一个合法 JSON 对象即使其字符串值内含 tab、` ``` ` 等字面字符仍被接受；Markdown/prose 只因无法解析成单个 JSON 对象才被拒绝。握手/决策阶段可用校验 API 强制类型与 `request_id`：

- `ParseReady(line)`：只接受 `ready`，本阶段出现 `action` 等其它类型被拒绝。
- `ParseAction(line, expectedRequestId)`：只接受 `action`（`ready` 被拒），且 `request_id` 必须等于期望值，否则拒绝。

## 动作与方向

动作 `move`/`interact` 必须带 `direction`；`pickup`/`wait` 不得带方向。方向为绝对四向：`north=(0,-1)`、`east=(1,0)`、`south=(0,1)`、`west=(-1,0)`。反馈 `status` 为 `applied/blocked/no_effect`。任务阶段为 `find_key/open_door/find_core/return_to_exit/succeeded`。

## 示例（手段的 wire 形状，非逐字节必等）

```json
{"type":"hello","protocol":"agent/1","actions":["move","pickup","interact","wait"]}
{"type":"observation","request_id":"r42","tick":42,"observation":{"position":{"x":8,"y":5},"tiles":[{"x":8,"y":5,"terrain":"floor"}],"inventory":["key"],"mission":"open_door","last_result":{"status":"blocked","reason":"closed_door"}}}
{"type":"episode_end","request_id":"r42","result":{"kind":"success"},"observation":{"position":{"x":1,"y":1},"tiles":[],"inventory":[],"mission":"succeeded"}}
```

Agent 回复：

```json
{"type":"ready","protocol":"agent/1","name":"explorer"}
{"type":"action","request_id":"r42","action":{"type":"interact","direction":"east"}}
```

## DTO 与实现

- 编码 DTO：`HelloMessage`、`ObservationMessage`、`EpisodeEndMessage`（Host→Agent）。
- 解析：`AgentResponseParser`（读取 `ready`/`action`），返回 `ReadyResponse` 或 `ActionResponse`。
- M3 Runtime已实现原始字节流式读取，完整行形成前限制64 KiB；Protocol保持单行解析与边界校验职责。
- fixtures：`tests/Fixtures/Protocol/agent/`。
合法单行 JSON 的前后空格及 tab 可接受；可带一组 LF/CRLF 行尾，未转义的内部换行或重复行尾拒绝。字符串 API 也拒绝无效 Unicode，管道路径使用严格 UTF-8 字节解析。

M3实际执行、共享期限、进程清理及run/1结果见 [runtime-execution.md](runtime-execution.md)。EOF半行与无响应分别作为传输错误/超时，不能补成动作。正常运行时只发送局部观测，参考解仍为内部验收工具。
