# 实施任务清单

依据：[项目实施计划](project-plan.md)。**M0–M5 的 30 项任务已通过本机 Windows 验收（T503 为部分完成）**；M6 进行中：T601/T602 的 Windows 部分与 T603 的生成扫描已完成，其余（Linux、性能基准、队列上界、发布包）未完成。验收细节见 [m0-m1.md](milestones/m0-m1.md)、[m2.md](milestones/m2.md)、[m3.md](milestones/m3.md)、[m4.md](milestones/m4.md)、[m5.md](milestones/m5.md) 与 [m6.md](milestones/m6.md)。协作方式见 [委派政策](delegation-policy.md)。ID用于后续提交与验收追踪，勾选只表示已通过所注明范围的验收。

## M0：契约与固定样例（1–2 日）

- [x] T001 — 确认 SDK 与依赖；建立 solution、四个生产项目和测试项目；固定 global.json/依赖版本，启用 nullable。验收：干净构建成功；Core/Protocol 无相互引用，Cli 无 Core 引用。
- [x] T002 — 编写 `docs/agent-protocol.md`：hello/ready/observation/action/episode_end、错误、字节/深度上限、联合期限。验收：Python 可以按样例手工响应，未知动作/方向/ID 错误有明确判定。
- [x] T003 — 编写 `docs/observer-protocol.md`：snapshot/status/step_batch、seq/tick/base_seq、补丁字段、视图记忆与注册。验收：列出初次/中途/重订阅样例，能手算重建状态。
- [x] T004 — 编写 `docs/replay-format.md` 与 `docs/scenario-format.md`：版本、字段、规范状态编码、错误策略、损坏处理。验收：完整和 incomplete 样例均有期望结果；规范编码字节序明确。
- [x] T005 — 制作两张手工小地图：正常任务与遮挡/闭门边界；附参考动作、期望回合与哈希输入；增加不可解和门可绕过的坏场景。验收：动作/观测可逐步人工解释，不依赖随机生成。
- [x] T006 — 落地 ADR 001–011，冻结坐标、视野角点、终局优先级、日志/错误边界。验收：规则细节无需由 UI 或 Agent 猜测，新增建议与报告原约束有区分。

阶段门槛：协议、地图与预期输出齐备；版本/上限无待决项。本轮已固定全局 SDK 10.0.401；M0–M1 使用零第三方依赖的 console 验收，Spectre 等依赖在实际需要的阶段引入。

## M1：纯 Core（2–3 日；依赖 M0）

- [x] T101 — 实现 Scenario、Grid、Position、Direction、Action 与 Game；连续地形数组、独立对象/库存。验收：对象格可被玩家占用，导出数据不暴露内部数组。
- [x] T102 — 实现固定规则与任务阶段。验收：拿卡开门取核心返程成功；撞墙/缺卡/空拾取消耗一回合；已结束 Step 拒绝。
- [x] T103 — 实现局部可见性与 Observe。验收：墙后/闭门后/角点遮挡、阻挡格可见、开门后新增范围；无隐藏字段。
- [x] T104 — 实现深度独立 Capture 与规范状态哈希。验收：外部修改或后续 Step 不改变旧快照；固定宽度/顺序/字节序 golden fixtures 一致。
- [x] T105 — 规则反例与边界验收。验收：最后允许回合成功优先；核心拾取未返程不成功；纯 Core 不加载 I/O/JSON/终端依赖。

阶段门槛：固定地图闭环通过且哈希可重复；本阶段不引入 Agent 进程、TUI 或生成器。

## M2：生成与求解（3–4 日；依赖 M1）

- [x] T201 — 实现 PCG32、初始化和命名流派生。验收：独立参考已知向量；map/objects/mission 流定义与字节序有文档。
- [x] T202 — 完整状态求解器使用同一 Core 转移。验收：参考动作含 pickup/interact 并经真实 Game 成功；不能只找到地形路径。
- [x] T203 — 任务拓扑生成、对象放置、有限重试与诊断。验收：门必经、门卡在门前、核心在门后；失败返回 seed/版本/重试次数。
- [x] T204 — Protocol 的 Scenario DTO 与 Runtime 文件映射；CLI generate/validate。验收：导出/导入同样状态；结构、任务约束、完整可解性三层校验；坏场景被拒绝。
- [x] T205 — 固定种子检查与参考路径预算。验收：100 seeds 全部可解，路径上限/重试配置有证据；失败不静默更换 seed。

阶段门槛：稳定生成可解场景；求解器仅用于内部，不给普通 Agent 参考答案。

## M3：Agent 与 Runtime（3–4 日；依赖 M2）

- [x] T301 — Agent JSONL 编解码与边界解析。验收：逐字节上限、严格 UTF-8、深度限制、空行策略、request_id 一致；非法响应不进入 Core。
- [x] T302 — 进程会话与生命周期。验收：ArgumentList 启动、握手、stdin flush、联合期限、stderr 持续排空与有限尾部、关闭/终止均有界。
- [x] T303 — 单局串行所有者和规范运行结果。验收：正常终止/截断/执行异常分列；最后有效动作和回合可定位；取消不继续推进。
- [x] T304 — `agents/random_agent.py` 与独立故障进程 fixtures。验收：随机 Agent 可正常结束运行；错 ID/重复/半行/不响应/超长/日志洪流不挂死。
- [x] T305 — 用 fixture 与CLI入口跑通Core↔Agent闭环；测试入口保留跨平台配置。验收：不依赖TUI；输出含错误类别，未完成记录功能不冒充可用；Linux与后代/孤儿进程清理留M6。

阶段门槛：协议及进程执行可靠；任务成功率不作为 Random 基线门槛。

## M4：Observer 与回放（4–5 日；依赖 M3）

- [x] T401 — 独立投影、不可变 Snapshot/StepBatch 与 VisualState reducer。验收：每步 reducer 结果等于重新投影；entity 删除、视野变化、库存/阶段更新覆盖。验收记录见 [m4.md](milestones/m4.md)（9 项 Observer 检查）。
- [x] T402 — Hub 与原子注册/重同步。验收：每订阅独立队列；base_seq+1 连续；容量 1–2 下脱离/重订阅明确，不继续错误世界（7 项 Hub 检查）。
- [x] T403 — 权威写入器、提交边界与 footer。验收：动作与全部 status envelope 无缺序；故障后不发布未记录步骤；摘要不暴露未提交 Core 状态（9 项集成检查，含“致胜步记录失败时 Agent 未收到 episode_end”）。
- [x] T404 — 回放 reader 与无损 Observer 导出。验收：无需 Agent/Core 即可播放；缺 footer/末行残缺标 incomplete；中间损坏停止并定位（29 项 Replay 检查）。
- [x] T405 — 动作重演 verify。验收：同版初始场景与动作逐步哈希/事件一致；篡改后指出首个不一致步的行号与 tick；版本不支持明确不能核验。
- [x] T406 — 集成 run/headless/record/replay/verify 与机器输出。验收：stdout JSONL 可解析，stderr 独立；慢 stdout 脱离不改固定动作结果；`--headless` 与 `--observer-stdout` 互斥；记录失败退出码一致（8 项 CLI 黑盒检查，真实子进程）。

阶段门槛：记录、重建和重演同时通过；不能用画面看似正确代替协议性质测试。

## M5：终端与探索基线（2–3 日；依赖 M4）

- [x] T501 — Spectre.Console 地图/状态/事件面板与 ASCII 格子。验收：Terminal 只消费 Observer DTO；未知/已见/当前可见区分；小终端退化可用。**实现偏离**：为保持零第三方依赖改用 ANSI + 自绘格子（`TerminalView.cs`），未引入 Spectre。Console；已见/当前可见用字形+暗色区分，96 种尺寸无越界（隔离检查 106 项），但真实终端观感未肉眼确认。
- [x] T502 — waiting/耗时显示与动画独立刷新。验收：不同刷新率不改固定动作哈希；elapsed 不进入 Core；慢读后跳过旧动画。`--tui` 用 `ReplayControls` 提供暂停/单步/倍率（0.25–16x）；慢订阅走 `resync_required` 而非应用错误状态。
- [x] T503 — 人工 `play` 与回放暂停/单步/倍率。验收：相同 Action 入口；human.jsonl 可离线播放并 verify。已实现：`play`（人工 13 回合记录可 `verify`）+ `replay <jsonl> --tui [--speed N]`（TerminalView 渲染 + `ReplayControls` 暂停/单步/倍率，重定向时按用法错误拒绝），并由 `M6ReplayCliChecks`（4 项）与 `M5CliChecks`（5 项）覆盖；终端观感仍未肉眼确认。
- [x] T504 — `agents/explorer_agent.py` 维护地图与返程规划。验收：只使用局部输入，在明确固定样例成功；不读取场景文件/seed/Oracle 路径。手工样例 13 回合最优（哈希等于 Core golden），固定种子集 9/9 通过（M5 Explorer 检查），空工作目录隔离运行也通过。

阶段门槛：完整演示可从生成一路走到回放与核验；人工、Random、Explorer 同规则。

## M6：发布与文档（2–3 日；依赖 M5）

- [ ] T601 — Windows/Linux 构建与固定动作 golden 核验。验收：逐步 Core 哈希与事件一致，执行命令与环境留档。**部分完成**：Windows 侧已由 `M6GoldenChecks`（4 项）覆盖（冻结向量、跨进程确定性、verify 只读确定、观察者不变性）；Linux 对照未做（本机无 Linux 环境）。
- [ ] T602 — 超时/取消的进程树清理和标准流黑盒测试。验收：包含派生子进程案例，无残留；若需平台补丁则先修复再发布。**Windows 部分完成**：`M6ProcessTreeChecks`（3 项）覆盖决策超时与拒绝关闭两条路径，并断言**孙进程**消失、夹具自身有效性；Linux 与 `setsid` 分离进程清理未验证。
- [ ] T603 — 固定 1000 seeds 生成检查、资源上限与分层性能基准。验收：留存失败清单/版本/机器/构建配置；记录与 Viewer 队列无持续无限增长。**已完成本机可做部分**：seed 0–999 全部可解（1000/1000、全部一次成功、最大参考路线 101、1.952 秒，`artifacts/m6-seed-sweep/report.json`）；`M6ResourceChecks`（4 项）验证容量 1 的慢订阅者有界且不影响规则结果、记录行数恒为 `2N+3`、字节/步 935.7 有界、重复 5 次无状态累积，并给出分层计时（`artifacts/m6-perf/report.json`）。Linux 环境下的对应测量未做。
- [ ] T604 — 发布包、依赖锁定、协议说明、快速上手与故障示例。验收：干净环境执行 generate→validate→run→replay→verify；可单独使用无需模型服务。

阶段门槛：所有 v0.1 完成标准通过，已知限制披露，命令示例实际运行。

## M7：多玩家（按需新阶段）

需求与决策见 [multiplayer-requirements.md](multiplayer-requirements.md)，验收记录见 [milestones/m7.md](milestones/m7.md)。已确认范围：本地多席位、首版协作（门卡/门共享、核心单一携带者）、固定席位（中途不加入/不重连）、首切片恰好 2 席；敌对/干扰作为后续 `adversarial` 规则版本。

- [x] T701 — Core 多席位规则与 `core-state/2`。验收：严格座次轮转（`tick % N`）、非本席回合拒绝且不变更状态、共享门卡/门、核心单一携带者、携带者到达出口才成功、末回合成功优先、哈希覆盖每个席位；v1 冻结不动，向量由独立 Python 实现产生。**已完成**：M7 11 项检查通过，冻结向量 `initial/carrier/success` 三态与 Python 生成器字节一致。
- [ ] T702 — 协议 v2 与每视图投影。验收：`agent/2`、`observer/2`、`replay/2`、`scenario/2` 成套装席位字段与 `view=agent:<seat>`；每视图 `base_seq` 连续；席位观测互不泄露；`facility-zero/1` 档案仍可 `verify`。
- [ ] T703 — 席位调度与混合席位。验收：每席位独立决策期限，超时按**已提交的 `wait`** 记录；静默席位不阻塞他人；1 人类 + 1 进程 Agent 的一局可记录、可离线 `replay`、可 `verify`；取消/超时清理 N 个进程树。
- [ ] T704 — 旁观视图与会话清单。验收：`view=spectator` 可见公开全图且绝不下发给 Agent；`--session seats.json` 描述席位种类（人类/Agent/只等待）；端到端 2 席位演示可复现。
- [ ] 后续（按需）— 中途加入/重连/置换、`adversarial` 规则版本（干扰、抢夺、碰撞）、3–4 席扩展、网络传输、PettingZoo 风格 RL 适配。

## 阶段验收记录模板

```text
里程碑 / 日期：
代码提交 / 规则 / 生成器 / 协议版本：
SDK / OS / 构建配置：
执行命令：
预期与实际结果：
失败反例 / 故障注入结果：
产物与日志位置：
未解决问题 / 是否阻止下一阶段：
```

后续实施时每次领取一个明确任务包，提交代码、实际命令、失败样例和验收结果。遇到范围变化，记录对规则、协议、回放与验证的影响后更新规划。
