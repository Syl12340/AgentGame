# Replay 格式（replay/1）

M0 已冻结 DTO、编码与消息 fixtures；文件写入、读取、`replay` 与 `verify` 命令已在 M4 实现并通过验收（见 [observer-runtime.md](observer-runtime.md) 与 [M4 验收](milestones/m4.md)）。

| 记录 | 字段与用途 |
|---|---|
| run_header | replay/agent_protocol/observer_protocol、rules、generator、core_encoding、agent_name、完整 scenario、initial_snapshot |
| status_record | observer_status：完整 agent_status envelope，原样保存 protocol/run_id/view/seq/tick/agent_status |
| step_record | seq、tick、规范 action、outcome、core_hash、可选 view_hash、完整 observer_batch |
| run_footer | status、result、last_tick、可选 stats |

status_record 是记录封装，observer_status.type 为 agent_status；离线导出直接抽取 envelope。step_record.seq 必须等于 observer_batch.seq，tick 同样一致。初始 Snapshot.base_seq=0 后的 waiting 为 seq=1，第一个步骤为 seq=2，不能覆盖快照序号。记录自身不另设一套同名 seq。

core_encoding 为 core-state/1，与规则/生成器/传输版本独立。Core 哈希保存每个已提交动作之后的状态，不能误填初始哈希。header 保存完整初始布局，不依赖未来生成器。Scenario 生成元数据不发送给 Agent。

播放只消费初始 ObserverSnapshot 和后续 envelope，不调用 Core 或 Agent。verify 独立使用同版初始场景和规范动作，检查每步 Core 哈希、动作反馈与领域事件。版本不支持时明确不能核验；支持旧 Observer 的 Viewer 仍可播放。事件使用语义映射，不能把 run_id、真实耗时或 seq 当作规则确定性数据。

result 为 success/turn_limit 等规则结果；执行异常可为 null，必须显式输出，不能写成环境失败。completed 表示正常规则结束；aborted 表示执行未正常完成，last_tick 为最后已提交回合。缺 footer/尾部残行标记 incomplete，可读完整前缀但不声称正常完成；中间损坏停止并指出行号。实际读写错误、flush 与损坏处理已在 M4 验收（29 项 Replay 检查 + 8 项 CLI 黑盒检查）：末行残缺 → `status=incomplete`/`error=incomplete` 且带 `line`；中间损坏 → `error=invalid_record` 且带 `line`；footer 之后仍有数据 → `error=data_after_footer`。

提交顺序是 Core 计算 → 权威记录成功 → 发布 → 下一请求。write/flush 失败立即中止，不发布未记录的步；已变更 Core 不回滚，因此摘要必须使用最后提交状态。普通缓冲成功不承诺断电持久性。

`tests/Fixtures/Protocol/replay/valid-replay.jsonl` 为手工场景的一步后异常停止示例：初始 base_seq=0、waiting seq=1、move east 的 step seq=2/tick=1、aborted footer/result=null。动作后哈希取独立 Python 向量 first_move_sha256；它是格式与规则输入 fixture，尚未通过一个已实现的 replay/verify 命令。

## 记录大小上限

Replay 单条 JSONL 记录的 UTF-8 内容上限为 8 MiB（8,388,608 字节），不计可选的单个 LF 或 CRLF 行结束符，为完整场景布局、Observer 快照或批次及记录封装留出空间。Observer 消息上限为 4 MiB，Agent 消息上限仍为 64 KiB；各边界独立校验，JSON 嵌套深度上限均为 32。
