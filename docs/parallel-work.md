# 并行实施分工（M0–M1）

日期：2026-10-02。SDK：全局 .NET 10.0.401。所有任务仅在本工作区内修改文件，不读凭据，不修改全局配置。

| 执行方 | 模型 | 独占修改范围 | 交付 |
|---|---|---|---|
| DSH | chatecnu/ecnu-max | src/AgentGame.Protocol/ 下的 .cs；tests/AgentGame.Protocol.Tests/ 下除 csproj 外的文件；docs/agent-protocol.md、observer-protocol.md、replay-format.md、scenario-format.md；tests/Fixtures/Protocol/ | 显式 DTO、严格 Agent 响应解析、三类协议与 Scenario 文档、协议 fixtures 与验收程序 |
| Antigravity | gemini-3.1-pro-high | solution、所有 csproj、Directory.Build.props、global.json、.gitignore；src/AgentGame.Cli/ 下的 .cs；docs/adr/；tests/AgentGame.Architecture.Tests/ 下除其他模块文件外的所有文件 | 四项目工程、CLI 帮助骨架、架构验收、11 项 ADR |
| Codex | 当前主代理 | src/AgentGame.Core/ 下的 .cs；tests/AgentGame.Core.Tests/ 下除 csproj 外的文件；tests/Fixtures/Core/；docs/core-rules.md、core-state-format.md、milestones/；README.md、backlog.md、project-plan.md、本文 | 纯规则/视野/快照/哈希、手工场景、规则验收、集成与文档状态 |

共享约定：Core 和 Protocol 都不引用其他项目。Runtime 引用 Core/Protocol，Cli 只引用 Runtime/Protocol。全局 net10.0、nullable、implicit usings，不使用外部 NuGet 包（本阶段只需基础库；测试为可运行的 console 契约检查，非 dotnet test）。Core 测试只引用 Core，Protocol 测试只引用 Protocol，Architecture 测试只使用基础库检查工程边界。

Runtime 本阶段仅工程空壳；Agent 进程、生成器、ObserverHub、权威记录与 TUI 未实施。各方不修改别人的目录，也不提前实现 M2–M6。由 Codex 最终 build/run 和审阅后更新验收状态，不以子代理自报通过为准。

## 本轮实际结果与所有权移交

- DSH / chatecnu/ecnu-max：写入 Protocol DTO/解析器、三份协议与 Scenario 文档、fixtures 和协议验收代码。两次调用分别出现 RPC 300 秒超时和任务 240 秒超时；没有将这些超时视为验收成功。主代理检查文件并修正空白/单行处理、显式 patch/null 清空、完整 status envelope、回放动作后哈希、生成元数据和共享配置所有权，最终 22 项协议检查通过。
- Antigravity / gemini-3.1-pro-high：两次工程任务与一次较小只读审查均返回“成功但空答案”，没有生成可核验工程或审查结果。主代理接管工程、CLI、架构验收与 ADR。不能把它记为已交付模块。
- Codex：完成纯 Core 与 26 项检查、独立 Python 编码向量、工程/CLI/架构补位，以及全量集成。四生产项目、三验收项目已构建成功；最终 63 项检查通过。

所有子代理调用已返回后，模块统一交由主代理集成维护。未修改调研报告；未部署或发布。后续再次并行时重新明确目录所有权并限制任务包，优先先验证代理能返回实际产物。

## M2 并行工作与验收（2026-10-03）

| 执行方 | 独占任务 | 实际结果 |
|---|---|---|
| DSH / chatecnu/ecnu-max | PCG32、命名流、randomness.md；末轮只读代码审查 | 交付随机模块；主代理发现第一步初始化被丢弃，反馈修正，并以独立Python向量验证。明确小端读取与bound=1消耗随机值。 |
| AGY / gemini-3.1-pro-high | 原计划Runtime映射；修复后改为generation.md文档模块 | 初始任务因headless写权限被拒绝未交付。桥接修复后实际写入生成文档，主代理核对并修正128步适用范围与输出契约。 |
| Codex | 求解器、任务校验与生成器；Runtime/CLI接管；测试、桥接排查与集成 | 完成103项项目检查和100种子真实执行；AGY桥接7项检查及真实只读/写入任务通过。 |

目录移交均在前一调用结束后进行，未并发改写同一文件。原始调研报告哈希保持不变。AGY全局桥接的本次修复属于用户要求的代理排查，原文件已备份；详情见 [agy-diagnostics.md](agy-diagnostics.md)。M2验收见 [milestones/m2.md](milestones/m2.md)，下一阶段M3尚未开始。

DSH末轮只读审查的两条疑点经主代理复核未成立：求解器每条边实际调用 `Game.Step`，并丢弃已截断状态，已有max_ticks=1与13/12边界检查；省略tick的去重键不等于省略回合限制。另一个128×128导致生成DTO超过64 KiB的判断也不成立：Runtime生成入口固定21×13且不暴露尺寸参数；Core最大ASCII地形仅16384字符。没有据此增加重复规则或修改已通过的转移逻辑。

## M3并行工作与验收（2026-10-03）

| 执行方 | 独占模块 | 实际交付与复核 |
|---|---|---|
| DSH / chatecnu/ecnu-max | agents/random_agent.py；tests/Fixtures/Agents/fault_agent.py | 生产随机Agent及18种初始故障模式；主代理检查源码，补齐显式UTF-8、生产宿主错误判定与4种后续测试模式。 |
| AGY / gemini-3.1-pro-high | Runtime/Agents/JsonLineTransport.cs | 初次终端命令被sandbox拒绝，桥接正确返回错误；限定仅文件工具后交付传输模块。主代理检查并改用固定行缓存、增加取消检查。 |
| Codex | AgentModels、进程会话、stderr尾部、GameRunner、CLI、测试和文档 | 运行/取消/超时/错误边界与真实进程验收；全部149项检查通过，外部13步成功哈希匹配Core golden。 |

AGY使用已修复桥接的新MCP进程，保留sandbox和任务级写入授权，没有扩大终端权限；DSH/AGY任务结束后再移交修改所有权。最终证据见 [milestones/m3.md](milestones/m3.md)，完整日志 `artifacts/m3-verification.log`。M4为下一阶段。

## M4 并行工作与验收（2026-10-03）

本轮改用四个并行的通用子代理（不再是 DSH/AGY 桥接），每个只负责一个独立产物，主代理负责接线、编译、运行与判定。**所有子代理在各自会话里都无法执行 shell（同一沙箱错误），因此它们只写文件、全部由主代理实际构建与运行验证**；子代理自报的“完成”一律不作为验收。

| 子代理 | 独占产物 | 主代理复核结果 |
|---|---|---|
| 只读审计 | 无（报告） | 提出 4 条：2 条成立（退出码文档冲突、`--headless` 无语义），2 条不成立（记录失败后发成功终局、场景静默截断）。逐条复核见 [m4.md](milestones/m4.md) |
| CLI 黑盒检查 | `tests/AgentGame.Runtime.Tests/M4CliChecks.cs`（507 行） | 交付时未编译：主代理修 5 处编译错误与 4 处错误断言（replay 导出行数按 envelope 类型计、篡改 wait 无变化、独立重演未覆盖哈希篡改、verify 首个不一致步的 tick 语义）后达 8/8 通过 |
| 探索 Agent（M5 前置） | `agents/explorer_agent.py`（795 行） | 未运行过；主代理用 CLI 实测：手工样例 13 回合最优（哈希等于 Core golden）、生成场景（seed 7，最短 45）45 回合最优，两次 `verify` 均 valid；非法参数时宿主报 `agent_exited` 且 tick 为 0 |
| 终端视图（M5） | `src/AgentGame.Cli/TerminalView.cs` | 本轮内交付，接线与验收在 M5 进行 |

本轮主代理另修改：`Program.cs`（`--headless` 语义与冲突校验、帮助文本退出码说明）、`tests/AgentGame.Runtime.Tests/Program.cs`（注册 CLI 检查）、`docs/project-plan.md` 第 9 节（退出码与命令面改为实际约定）、`README.md`、`docs/backlog.md`、新增 [observer-runtime.md](observer-runtime.md) 与 [milestones/m4.md](milestones/m4.md)。

环境限制：本会话的命令沙箱最初完全不可用（`sandbox-local windows-acl temp grant materialization failed`），放宽权限后只有 Windows PowerShell 5.1、找不到 pwsh 7，因此 `scripts/verify.ps1` 等需要 PowerShell 7 的 30 项 CLI 黑盒检查**本轮未重跑**；M4 的同类覆盖改由 Runtime 项目的 C# 黑盒检查承担（8 项，真实子进程）。原调研报告哈希保持不变。

M4 验收结论与完整证据见 [milestones/m4.md](milestones/m4.md)：四个验收程序 203 项全部通过，构建 0 警告 0 错误。

## M5 并行工作与验收（2026-10-03）

**模型路由调整（用户要求）**：本会话的内置 `subagent` 工具没有模型参数，无法直接指定模型，因此 M5 后期改用两种更省的委派通道：

- `workflow` 的 `agent(prompt, { provider: 'chatecnu', model: 'ecnu-max' })`：DSH 内置子代理可调用的校内免费额度模型。已用探针验证（返回 `ECNU_OK`），M5 的两个只读审查即由此完成。
- AGY 桥接：`artifacts/agy-diagnostics/dispatch-task.mjs`（可复用脚本，`--task-file/--model/--timeout/--readonly`），模型 `gemini-3.1-pro-high`（别名 `pro`）或 `gemini-3.8-flash-high`（别名 `cheap`）。`ReplayControls.cs` 由此交付，AGY 自带 accept-edits 授权。

| 执行方 | 独占产物 | 主代理复核结果 |
|---|---|---|
| 内置子代理 | `src/AgentGame.Cli/TerminalView.cs`（~740 行） | 交付时在仓库外隔离工程实测：编译 0 警告、106 项行为检查通过（96 种尺寸、纯文本无 ESC、真实 fixture 渲染）。**发现并报告**冻结 fixture 的 `entity_removals:["guard_1"]` 指向不存在实体，会被 reducer 拒绝——主代理已修 fixture 并加回归检查（M4 Observer 10/10） |
| 内置子代理 | `src/AgentGame.Runtime/HumanSession.cs`、`src/AgentGame.Cli/HumanInput.cs` | 自带 40 项仓库外断言；主代理端到端验证：重定向 `play` 完成 13 回合、记录 `verify` 通过 |
| 内置子代理 | `tests/AgentGame.Runtime.Tests/M5ExplorerChecks.cs` | 该子代理在交付前被中止（无结束消息），但其文件可用：注册后随修复后的探索 Agent 达到 **9/9 通过** |
| 内置子代理 | `agents/explorer_agent.py` 活锁修复 | 同样在交付前被中止；主代理实测其修复已落地：手工样例恢复 **13 回合最优**（哈希等于 Core golden）、12 种子中不再出现原地重复失败动作 |
| 内置子代理 | `tests/AgentGame.Runtime.Tests/M5CliChecks.cs` | 自带 5/5 真实子进程实测（`--tui` 重定向拒绝、互斥、`play` 记录+verify、参数错误、无 ANSI） |
| AGY / gemini-3.1-pro-high | `src/AgentGame.Cli/ReplayControls.cs` | 一次交付成功；主代理编译通过并在 `run --tui` 中接线使用 |
| ecnu-max 只读审查 ×2 | 无（报告） | 代码审查发现一个**真实缺陷**：`play` 模式下 sink 的按键轮询与 `HumanInput` 争抢同一个控制台（已修：新增 `interactiveControls`）；文档审查发现 README/terminal.md 的过时与不符表述（已修）。另有两条未验证疑虑记录在 [m5.md](milestones/m5.md) |

主代理本轮另修改：`Program.cs`（`--tui`、`play`、帮助文本、退出码说明）、`src/AgentGame.Cli/TerminalObserverSink.cs`（新文件，实时观察者 + 控制权开关）、`docs/terminal.md`、`docs/replay-format.md`、`docs/observer-protocol.md`、`docs/runtime-execution.md`、`docs/observer-runtime.md`、`docs/project-plan.md`、`README.md`、`docs/backlog.md`，新增 [milestones/m5.md](milestones/m5.md)，并修复冻结 fixture。

M5 验收结论见 [milestones/m5.md](milestones/m5.md)：四个验收程序 **218 项**全部通过（Core 39、Protocol 44、Runtime 123、Architecture 12），构建 0 警告 0 错误。原调研报告哈希保持不变。

## M6 并行工作与验收（2026-10-04）

从本轮起主代理转入**高级审查**角色，并执行 [委派政策](delegation-policy.md)：产出交给廉价通道，主代理负责审查、修错与独立复现。**不再使用内置 `subagent` 工具**（它计费在 ds 账号上）。

| 执行方 | 独占产物 | 主代理复核结果 |
|---|---|---|
| ecnu-max（1 个自验证代理） | `tests/AgentGame.Runtime.Tests/M6GoldenChecks.cs`（4 项） | 该代理用仓库外宿主工程自跑 4/4；主代理注册进 `Program.cs`、重新构建并运行 → **4/4 通过**，并逐条审阅断言强度（记录文件用**逐字节**比对而非只比长度） |
| AGY / gemini-3.1-pro-high（只写文件） | `tests/AgentGame.Runtime.Tests/M6ProcessTreeChecks.cs`、`tests/Fixtures/Agents/descendant_agent.py` | 编译 0 警告、运行 **3/3 通过**；主代理核验：夹具先派生孙进程并 fsync pidfile 再握手；检查 1 用 `agent_process_id` 与 pidfile 绑定；检查 3 单独证明孙进程确实存活过，避免"夹具没跑起来"的假通过 |
| ecnu-max（测量代理） | `artifacts/m6-seed-sweep/report.json` + 临时 harness | 自报 1000/1000；主代理**独立重跑 harness**（同样 1000/1000、最大参考路线 101、全部一次成功、1.952 秒）并用 CLI 抽查 seed 0/1/250/500/750/999 全部生成且可验证 |
| 主代理 | `Program.cs` 注册、`docs/milestones/m6.md`、README/backlog/project-plan 计数同步 | 225 项检查通过 |

能力探测（本轮）：**ecnu-max 子代理具备 shell**（实测 `dotnet --version` → `10.0.401`），因此可自验证的任务优先派给它；AGY 的终端命令仍被沙箱拒绝，只能写文件。两类的产物主代理都必须独立复现一次才算验收。

M6 现状见 [milestones/m6.md](milestones/m6.md)：225 项检查通过（Core 39、Protocol 44、Runtime 130、Architecture 12）；Linux、分层性能基准、队列增长上界、发布包与 pwsh 7 脚本重跑仍未完成。

## M5/M6 收尾（2026-10-04，高级审查模式）

| 执行方 | 独占产物 | 主代理复核结果 |
|---|---|---|
| ecnu-max | `tests/AgentGame.Cli.Tests/`（新项目）+ `src/AgentGame.Cli/AssemblyInfo.cs`、`AgentGame.slnx`、`tests/AgentGame.Architecture.Tests/Program.cs`、`scripts/verify.ps1` | 自报 6/6 与 Architecture 13/13；主代理重新构建并运行 → `M5 Terminal: 6/6`、`Architecture: 13/13`；核对其对 resync 帧的断言适配与 `docs/terminal.md` 描述一致 |
| ecnu-max | `tests/AgentGame.Runtime.Tests/M6ResourceChecks.cs`、`artifacts/m6-perf/report.json` | 自报 4/4；主代理注册、构建、运行 → 4/4，但**发现度量缺陷**：亚毫秒计时被四舍五入成 0，基准不可用。已修为"秒（6 位小数）+ 微秒"并改掉 `*_total` 误导字段名，重跑得到真实数值（200 步 52 µs、1000 哈希 431 µs、verify 3 ms、整局 CLI 0.693 s） |
| 主代理 | `src/AgentGame.Cli/ReplayPlayer.cs`（新）、`Program.cs` 的 `replay` 参数解析、`tests/AgentGame.Runtime.Tests/M6ReplayCliChecks.cs` | 完成 T503 剩余部分：`replay <jsonl> [--tui] [--speed N]`；4 项新增检查覆盖导出、`--tui` 重定向拒绝、`--speed` 依赖与取值、未知选项；`--speed` 无 `--tui` 时明确报错 |

累计：**240 项检查通过**（Core 39、Protocol 44、Runtime 138、Cli 6、Architecture 13）。本轮仍未使用内置 `subagent`（额度经济性），全部委派经 ecnu-max；AGY 本轮未使用（无"只写文件"型任务）。

## M7.1 多玩家 Core（2026-10-04）

| 执行方 | 独占产物 | 主代理复核结果 |
|---|---|---|
| ecnu-max（需求研究，只读 ×2） | 无（报告） | 代码影响面（file:line 触点、会失效的单机检查、三大陷阱）与设计空间对比（轮转 vs 同时提交、共享 vs 携带、生命周期、本地 vs 网络）；结论与冻结报告第 15.8 节的判据一致，已并入 [multiplayer-requirements.md](multiplayer-requirements.md) |
| ecnu-max（M7.1 实现，自验证） | `src/AgentGame.Core/Multiplayer/*`、`tests/AgentGame.Core.Tests/M7MultiSeatChecks.cs`、`tests/Fixtures/Core/generate_vectors_v2.py` + `core-golden-v2.json`、`Core.Tests/Program.cs` 注册 | 复核范围（v1 Core 一字未改）、构建、跑全量、**独立重跑 Python 向量生成器并确认字节一致**；**发现真实规则漏洞**：核心被当作全队共享，队友先占出口 + 另一席取核心即可获胜，返程要求被绕过 |
| ecnu-max（漏洞修复，自验证） | 同上文件（改为"单一携带者"规则：`core-state/2` 用携带席索引 `core_holder`（-1 = 无人）取代共享布尔） | 独立复现：Core 39 + **M7 11/11**、其余四工程全绿；向量两次重跑 SHA-256 稳定且与提交文件一致；回归检查确实按漏洞场景构造（队友先到出口 → 断言仍未结束，携带者到出口才成功） |

另：本轮把一个**越权产物** `docs/agy-subagent-standalone-research.md`（某子代理未经任务授权写入 docs/，内容是关于 AGY 独立闭环的研究草稿）移出到 `artifacts/agy-diagnostics/standalone-research-draft.md`。docs/ 下的文档保持"已审阅"状态，未审阅草稿不放行。

累计：**251 项检查通过**（Core 50、Protocol 44、Runtime 138、Cli 6、Architecture 13）。

## M7.1b / M7.2a（2026-10-04，全部委派为代码任务）

按用户要求，本轮所有子代理都只领**写代码**的任务（不再派只读盘点/报告类任务）：

| 执行方 | 独占产物 | 主代理复核与修错 |
|---|---|---|
| ecnu-max | `MultiScenarioGenerator.cs`、`MultiSolver.cs`、`MultiScenarioValidator.cs`、`M7MultiGenerationChecks.cs` + Core.Tests 注册 | 复核后确认可用；其自报的诚实发现（双席全状态搜索随地图膨胀，多人生成默认棋盘降为 15×9）已写入验收记录的限制 |
| AGY / Gemini **Flash High** | `tests/AgentGame.Core.Tests/M7MultiSeatEdgeChecks.cs`（8 项边界检查） | 我修掉 1 处可空解引用编译错误；随后 1 项失败经定位是**检查脚本自身**让席位穿过关闭的门取卡（并非产品缺陷），改为走开放行并补全"非携带者站出口不结束"断言；现 8/8 |
| ecnu-max | `MultiRandomStreams.cs`（公共派生帮助类）+ `M7StreamEquivalenceChecks.cs`（2 项，5 种子 × 3 流 × 16 输出与冻结 v1 API 逐输出比对） | 我发现的重复派生风险（生成器复制了冻结 v1 的公式）由此被钉死；生成器改用公共帮助类 |
| ecnu-max | `src/AgentGame.Protocol/Multiplayer/*`（协议 v2 五类文件）+ `M7ProtocolV2Checks.cs`（14 项） | 复核：v1 编解码/夹具零改动，Protocol 合计 58 项全过；其报告的"另一代理文件的可空错误"由我修复 |

另：AGY 本轮又**越权**在 `docs/` 写了一份未审阅草稿（bridge 并行笔记），已移出到 `artifacts/agy-diagnostics/bridge-parallel-draft.md`；`docs/` 只保留已审阅文档。

累计：**280 项检查通过**（Core 65、Protocol 58、Runtime 138、Cli 6、Architecture 13）。

## M7.2b：每视图 Observer（本次续作）

以项目更新后的 `090ed16` 为基线继续。两个 DSH/ecnu-max 任务并行交付：投影 `MultiObserverProjection.cs`；归并器和 Hub `MultiVisualState.cs`、`MultiObserverHub.cs`。测试委派超时且未写入文件，未计为完成。主代理实现准备/提交协调、14 项验收与入口，并审阅所有实际文件。

初始 AGY Pro High 两个实现任务均因服务端地区限制失败，无交付；明确报告后改由 DSH 实施，没有修改账号或网络。用户要求重试后，无项目内容的连接测试实际返回 `AGY_OK`。自动审批拒绝了具体源码审查，理由是私有源码外发授权不明确；该审查没有启动，也不能计为通过。

主代理修正公开事件的 reason 过滤，并复用准备阶段已验证的归并器；新增检查先复现、再修正 v2 的溢出席位 ID 和事件跨席引用漏洞。Release 构建零警告、零错误；五程序 **294/294**（Core 65、Protocol 58、Runtime 152、Cli 6、Architecture 13）。本切片未新增内置子代理。多人调度、v2 文件与 CLI 接线待 M7.3–M7.4，见 [M7验收](milestones/m7.md)。

### 源码授权后的补充审查（2026-10-04）

用户明确授权 DeepSeek Harness 和 Antigravity 处理本项目源码，已记入 [委派政策](delegation-policy.md)。DSH 只读审查准备/提交、归并和 Hub；AGY Pro High 只读审查 v2 codec，以及投影的 `Events` / `BuildAgent`。两个通道均未报告范围内的具体新缺陷，主代理核对了相关实现。审查没有修改产品代码或运行测试，沿用上述已完成的 294 项验收。

AGY bridge 1.4.0 的后台审查任务 `1fe1da2e-4178-4c49-9ab0-d0f4c571f59b` 因 worker 退出成为 `interrupted`，没有结果，原因未确认。单文件同步审查完成 codec（会话 `0857691b-4224-47a2-a2e0-b160b2d3810a`）；投影首次同步调用超时，缩小到两个方法并继续原会话 `43857ee6-032e-43a0-8403-0c14dd551bd5` 后交付。失败调用均未计为完成，没有更换账号、网络或全局权限。

### M7.3 分工（进行中）

DSH 两项任务分别独占多人场景服务及 v2 记录/回放文件、多人运行器及提交器；AGY 独占 agent/2 进程方法、混合席位来源和对应检查。任务书位于 `artifacts/agy-diagnostics/task-m73-*.md`。主代理负责注册验收、检查实际改动和独立复现。本轮使用 bridge 1.5.0 的任务命令授权；AGY 仅获得 Runtime 项目的精确构建命令，不授予全局终端权限。

AGY 任务 `bfd7a0c5-d8a2-4c66-9b88-4b3424973b2c` 返回 `failed` / `denied_actions: escalate_admin`。命令审计先拒绝一条未授权命令（仅保存摘要），再允许精确的 Runtime 构建命令，但 Windows 终端沙箱要求交互式管理员初始化，未完成构建。主代理已向用户说明并请求初始化许可，未自动提权或重试。实际只写出部分 V2 进程方法，且命名空间与返回 API 错误，不能视为交付；检查后明确改派 DSH 补齐该文件及剩余独占产物。审计原始结果保存在 `artifacts/agy-diagnostics/m73-seat-sources-agy-failure.json`。

### M7.3 交付与独立验收

| 执行方 | 实际产物 | 验收状态 |
|---|---|---|
| DSH：场景/回放长任务 | `MultiScenarioService.cs` 与 v2 writer/reader/service 四文件 | MCP 调用超时，无结束报告或约定的测试文件；主代理检查已写文件，补齐不存在视图检查，并纳入真实运行/篡改验收 |
| DSH：席位来源接手 | `MultiSeatSources.cs`、v2 进程方法、`M7SeatSourceChecks.cs`、Python v2 fixture | 调用超时，无结束报告；主代理修正握手进程所有权、席位绑定及检查日志读取时机，独立运行 **9/9** |
| DSH：调度长任务及缩小后的单文件任务 | 无 `MultiGameRunner.cs` 交付 | 均超时；不计为完成，主代理直接补齐调度器与模型 |
| AGY / Pro High：仅提交器、非交互 | `MultiRunCommitter.cs`；任务 `f416e0a4-e5ac-4c51-8334-28b931f9b460`，会话 `7b1a4347-e888-40b8-afeb-90da687be1bf` | worker 返回 completed；空命令清单下的一次终端尝试被审计拒绝，未执行。主代理修正两处错误 Observe 调用，实际构建并验证失败时不发布 |
| 主代理 | 缺失的运行器/模型、`M7RuntimeChecks.cs`、验收注册与文档 | **16/16**：混合来源、三档案视图、超时 wait、双进程树取消、记录/flush 边界、篡改定位、缺尾部恢复及旧版回归 |

所有失败任务停止后才接管其文件，没有并发覆盖另一个写入者。完整 Release 构建 **0 警告 / 0 错误**；五程序 **319/319**（Core 65、Protocol 58、Runtime 177、Cli 6、Architecture 13）。原调研报告与冻结向量保持不变。当前可运行的是双席位库，CLI 会话入口待 M7.4，不能把测试中的人类回调当成已交付的终端界面。

用户同意一次 AGY 交互式沙箱初始化，主代理按许可启动仅执行 `dotnet --version` 的交互窗口；随后用户明确要求未来不再使用交互式 AGY，已保存。最后的非交互 Flash High 精确命令探针仍返回 `denied_actions: escalate_admin`（会话 `a59a7029-8f65-41c5-af76-9b0a84a8b2dd`）；命令授权通过，但 OS 初始化及执行未成功验证。不再次开启交互窗口、修改全局权限或改变账号/网络；本轮构建/测试均由主代理实际完成。

收尾补齐启动前取消时对注入 writer 的释放，并独立重跑完整 Runtime：177/177。构建诊断日志保留在本地忽略目录 `artifacts/agy-diagnostics/dsh-build-logs/`，不作为源码交付；最终验收日志与计数见 `artifacts/agy-diagnostics/m73-runtime-verification.log`、`m73-validation.json`。

## 外部 LLM Agent 主动控制接口（2026-10-04）

用户明确选择：外部 Agent 主动与运行中的游戏交互，游戏端不调用 LLM API。实现持久单人 `serve`、loopback HTTP 与 stdio MCP 工具入口。未改动用户已有的 `tools/bot` 文件，也未修改客户端全局配置。

| 执行者 | 独占文件 | 实际交付与检查 |
|---|---|---|
| DSH / ecnu-max | `src/AgentGame.Runtime/External/ExternalGameSession.cs` | 按限定任务交付单文件，未执行编译/测试；主代理检查并修正终局查询缓存、重复释放、释放失败上报、输入缓冲区所有权、启动异常资源释放与终局时释放记录文件 |
| AGY / Gemini Flash High | `tools/game-mcp/server.py` | 非交互 MCP 后台任务 `8d85b9aa-10a3-4d31-b0e5-5488959cef7d` 完成；conversation `bcf77c27-96c1-45c3-b5ff-ce6a174164b3`，`command_audit=[]`，没有终端操作或交互窗口；主代理修正错误方向枚举、初始化守卫、null 方向参数、非有限数/非法 Unicode 与 HTTP 错误资源释放 |
| 主代理 | HTTP 服务、CLI/FrameworkReference、验收与说明 | Kestrel 只绑定 IPv4 loopback；原记录/Observer/核验链路；新增 10 项 Runtime 会话检查、10 项真实 HTTP/MCP 检查；统一脚本接入并修正原有 `play` 无参数检查的过时期待 |
| DSH / ecnu-max（只读复核） | 无文件写入 | 复核提交边界、并发与关闭；指出缺少“终局后 footer/释放失败”覆盖。保留已提交规则结果与会话执行错误分别报告的语义，并追加该故障检查与说明；没有按建议丢弃已提交的成功结果 |

主代理独立 Release 构建：**0 警告 / 0 错误**。完整统一脚本通过：五程序当时 **328/328**（含最初 9 项外部会话检查）、真实 HTTP/MCP **10/10**、额外 PowerShell CLI **30/30**。随后只追加终局故障检查，重新构建并独立运行外部会话 **10/10**；当前五程序检查总数 **329**（Runtime 187）。生产代码与前述完整回归一致，无需重复完整种子/进程回归。日志：`artifacts/agy-diagnostics/external-control-verification.log`，聚合记录：`external-control-validation.json`。

真实 HTTP 客户端与独立 MCP 客户端分别完成 13 回合通关，服务保持运行时记录可 `verify`；覆盖局部视野、无动作等待、客户端退出/重新连接、竞争/过期拒绝、非法动作、协议初始化、传输失败、取消与权威写入失败。原研究报告 SHA256 保持 `FE4C9932C1E44A786AF0597C69436706545706704E42126441470CA10772F73C`；原 Core 和 agent/1 协议源/冻结向量未改动。使用方式见 [external-agent-control.md](external-agent-control.md)。多人外部控制入口与 M7.4 多人 CLI 仍待实现，终端实际屏幕观感未进行人工验收。

### 推送前验收（2026-10-05）

完整执行 `scripts/verify.ps1`：Release 构建 **0 警告 / 0 错误**；五程序 **329/329**（Core 65、Protocol 58、Runtime 187、Cli 6、Architecture 13）、真实 HTTP/MCP **10/10**、额外 PowerShell CLI **30/30** 全部通过，共 369 项。日志留在本地忽略目录 `artifacts/agy-diagnostics/push-verification-2026-10-05.log`。本次阶段提交涵盖多人 Observer/Runtime/回放与单人外部控制；独立的 `tools/bot/agent.js`、`tools/bot/bridge.js` 保留在本地。整理了 CLI 帮助中过时的阶段标识和多人文档中历史验收计数的措辞。多人 CLI M7.4、Linux 对照和终端人工观感验收仍保持待办。
