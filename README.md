# Agent Game / Facility Zero

面向脚本和后续 LLM/RL Agent 的程序生成回合制设施探索游戏。当前已通过 **M0–M5**（M5 的回放播放控制尚未接线）：工程、协议、纯 Core、场景生成与求解、Agent 子进程执行、Observer 投影、权威记录、离线回放与动作核验、终端实时观察者、人工模式与探索基线。

Core 支持局部探索、拿门卡、开必经门、取核心与返程成功。它采用整数规则、单局串行所有权、不可变外发快照和独立于 JSON 的规范状态哈希。

## 构建与验收

需要全局 .NET SDK **10.0.401** 与 Python 3（本机验收 3.13.0）。本阶段无第三方 NuGet 或 Python 包依赖。测试默认调用 `python`，可用 `AGENT_GAME_PYTHON` 指定其它解释器。验收是 console 契约程序，不是 `dotnet test`。

```powershell
dotnet build AgentGame.slnx -c Release
dotnet run --project tests/AgentGame.Core.Tests -c Release --no-build
dotnet run --project tests/AgentGame.Protocol.Tests -c Release --no-build
dotnet run --project tests/AgentGame.Runtime.Tests -c Release --no-build
dotnet run --project tests/AgentGame.Cli.Tests -c Release --no-build
dotnet run --project tests/AgentGame.Architecture.Tests -c Release --no-build
pwsh -File scripts/verify.ps1        # 需要 PowerShell 7；含 30 项 CLI 黑盒检查
```

五个验收程序共 **280 项**：Core 65（v1 单机 39 + 多席位 11 + 多人生成 5 + 边界 8 + 流派生等价 2）、Protocol 58（v1 44 + 协议 v2 14）、Runtime 138、Cli 6、Architecture 13。失败返回非零。`scripts/verify.ps1`（另含 pwsh CLI 黑盒检查）需要 PowerShell 7，因为用到了 `ProcessStartInfo.ArgumentList` 与 `Kill(entireProcessTree)`。固定种子 0–99 全部可解，最长参考路线 97 回合，均在第 1 次生成成功；**seed 0–999 全部可解**（最大参考路线 101、全部一次生成成功，证据 `artifacts/m6-seed-sweep/report.json`）。

## 命令面

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
dotnet run --project src/AgentGame.Cli -c Release -- scenario generate --seed 42 --out artifacts/facility42.json
dotnet run --project src/AgentGame.Cli -c Release -- scenario validate artifacts/facility42.json
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario artifacts/facility42.json --headless --record artifacts/run.jsonl -- python -u agents/random_agent.py --seed 42
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario artifacts/facility42.json --observer-stdout --record artifacts/stream.jsonl -- python -u agents/explorer_agent.py
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario artifacts/facility42.json --tui -- python -u agents/explorer_agent.py
dotnet run --project src/AgentGame.Cli -c Release -- play --scenario artifacts/facility42.json --record artifacts/human.jsonl
dotnet run --project src/AgentGame.Cli -c Release -- replay artifacts/run.jsonl
dotnet run --project src/AgentGame.Cli -c Release -- replay artifacts/run.jsonl --tui --speed 2
dotnet run --project src/AgentGame.Cli -c Release -- verify artifacts/run.jsonl
```

- 生成拒绝覆盖已有文件；默认 21×13 地图、512 回合、参考路径 ≤128 步、最多尝试 16 次；输出 JSON 摘要，不导出参考动作。
- `run` 用外部 Agent 的 JSONL stdin/stdout，打印一行 `run/1` 摘要。`--observer-stdout` 独占 stdout 并把摘要改送 stderr；`--headless` 表示不挂终端旁观者；`--tui` 渲染实时地图并支持暂停/单步/倍率（需要交互终端，与另两者互斥）。
- `play` 让你自己玩：方向键/WASD 移动、`e`+方向或 Shift+方向 交互、空格 pickup、`.` 等待、`q`/Esc 退出；`--record` 出来的记录同样可以 `verify`。
- `replay` 不需要 Core 或 Agent 即可播放；`verify` 用同版规则与编码逐步重演并核对哈希、反馈、事件与补丁，首个不一致步给出行号与 tick。
- 退出码：0 正常结束（含规则失败/回合截断）、1 运行失败或记录不可核验、2 参数错误、130 取消。原因细分看 JSON（`kind`、`error.code`、`valid`、`status`、`line`）。
- 随机 Agent 达到回合上限属于正常基线结果，成功率不作为门槛。手工样例的最短完整任务是 13 回合；`agents/explorer_agent.py` 实测在手工样例 13 回合、生成场景 45 回合（均为最短路线）完成任务。

## 工程边界

- `AgentGame.Core`：规则、状态、局部可见性、规范编码、PCG32、生成与求解，不引用其它项目。
- `AgentGame.Protocol`：显式 DTO、严格 Agent/Scenario/Observer/Replay 解析与编码，不引用 Core。
- `AgentGame.Runtime`：场景文件边界、DTO 映射、三层验证、Agent 进程生命周期、单局串行执行、Observer 投影/Hub、权威记录、回放与核验。
- `AgentGame.Cli`：只引用 Runtime/Protocol，提供帮助、版本、场景、运行、回放与核验命令；终端只消费 Observer DTO。

## 项目资料

- [技术研究报告](agent_game_research_2026-10-02.md)
- [项目实施计划](docs/project-plan.md) 与 [任务清单](docs/backlog.md)
- [并行分工与实际结果](docs/parallel-work.md)、[AGY 子代理排查与修复](docs/agy-diagnostics.md)
- 验收记录：[M0–M1](docs/milestones/m0-m1.md)、[M2](docs/milestones/m2.md)、[M3](docs/milestones/m3.md)、[M4](docs/milestones/m4.md)、[M5](docs/milestones/m5.md)、[M6（部分）](docs/milestones/m6.md)
- [多玩家需求研究](docs/multiplayer-requirements.md)（新阶段：席位模型、行动顺序、信息隔离与验收计划）
- [M7 验收记录](docs/milestones/m7.md)（多玩家阶段；M7.1 Core 多席位 + `core-state/2` 已完成，协议/运行时/终端仍在后续切片）
- 协作方式：[委派政策](docs/delegation-policy.md)（谁做什么、走哪条廉价通道、谁负责验证）
- 运行时说明：[会话、错误与运行摘要](docs/runtime-execution.md)、[Observer/记录/回放](docs/observer-runtime.md)、[终端视图](docs/terminal.md)
- 规则与格式：[Core 规则](docs/core-rules.md)、[规范状态编码](docs/core-state-format.md)、[随机流](docs/randomness.md)、[生成/求解](docs/generation.md)
- 协议：[Agent](docs/agent-protocol.md)、[Observer](docs/observer-protocol.md)、[Scenario](docs/scenario-format.md)、[Replay](docs/replay-format.md)
- [ADR 001](docs/adr/001.md) 起的 11 项架构决策

下一实施包为 M6：Windows/Linux 构建与 golden 核验、进程树清理黑盒、1000 种子与性能基准、发布包与快速上手文档。剩余已知项：`replay --speed/--pace` 播放控制、真实终端观感确认。v0.1.1 增加网页旁观，v0.2 增加环境控制/Gymnasium 与配对评测。

当前验收环境为 Windows。Linux 对照、完整后代/孤儿进程清理与跨平台进程树验证仍属 M6 发布门槛，不宣称已完成。
