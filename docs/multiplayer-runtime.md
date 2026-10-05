# 多人 Runtime（M7.3）

当前实现是 **两席位的库入口**，CLI 会话清单及本地人类终端接入待 M7.4。v1 `run` / `play` / `replay` / `verify` 保持原路径。

`MultiGameRunner.RunAsync` 接收 `MultiScenarioDto`、两个不同的 `IMultiSeatSource`、`AgentTimeouts`、取消令牌与 `MultiRunOptions`。运行开始前冻结场景及席位数组并校验任务拓扑、预算、参考长度和选定视图，随后只有同一逻辑属主调用 `MultiGame.Step`。

| 席位来源 | 行为 |
|---|---|
| `ProcessMultiSeatSource` | 复用既有 JSONL、期限、stderr 排空及进程树清理；使用 `agent/2` hello/ready、观测及动作 |
| `HumanMultiSeatSource` | 异步回调接收本席 `AgentV2ObservationMessage` 并返回规范动作；调用方负责输入交互 |
| `WaitMultiSeatSource` | 不创建进程，固定提交 `wait` |

一步属于 `tick % 2`。握手失败、决策超时、协议错误、退出或非法回调动作会关闭该来源，保留席位诊断，并将该席位本次及以后的回合真正提交为 `wait`；首版不尝试恢复连接或再次询问已超时的来源，防止迟到响应进入下一次请求。健康席位继续运行。回调需要异步返回；运行器可以停止等待异步任务，无法抢占回调中同步阻塞的用户代码。

全局取消停止在下一个规则推进前，并关闭所有来源。关闭过程有界；清理失败会变成运行错误，保留已提交规则结果。来源由运行器所有并且关闭一次，不能跨席位或跨运行重复使用。

`MultiRunOptions.RecordPath` 与 `ReplayWriter` 互斥，默认档案视图为 `spectator`，也可选择 `agent:0` / `agent:1`。**一个 `replay/2` 文件保存一个选定视图**，同时保存所有席位动作、反馈及逐步 `core-state/2` 哈希；其他实时视图从同一 Core 步派生。每步先准备全部投影，成功写入并 flush 选定视图的权威记录后才提交/发布，最后更新运行摘要。记录失败闩锁，不再发布或追加 footer，摘要用最后提交的快照。footer 的 stats 保存每席位名称、退役原因/请求 ID 与清理诊断。

`ObserverReady` 在 header 记录成功后获得 `MultiObserverSession`，可注册三个独立 Hub；应迅速返回并在其他任务消费。运行器只向选定记录视图发布等待/停止状态，因此不同视图的 seq 可以不同。

`MultiReplayFileWriter` 创建新文件、不覆盖；`MultiReplayReader` 增量读取有界 JSONL，检查身份、序号、tick、席位轮转与 footer，拒绝完整损坏行及 footer 后数据，未换行的尾部只保留已完整的前缀。`MultiReplayService.Export` 仅提取记录中的 Observer envelope，不构造 Core；`Verify` 则重演每席位动作并比对哈希、反馈、事件、补丁及初始视图。未知规则元数据可以播放，但不能核验执行。

真实检查入口：

```powershell
dotnet build AgentGame.slnx -c Release
dotnet run --project tests/AgentGame.Runtime.Tests -c Release --no-build -- --m7-sources-only
dotnet run --project tests/AgentGame.Runtime.Tests -c Release --no-build -- --m7-runtime-only
```

9 项席位来源检查及 16 项多人运行/回放检查通过；Windows 上验证混合运行 25 步成功、三个档案视图独立核验、超时提交 wait、双进程树取消清理、flush 失败的发布边界、篡改定位及旧版回归。M7.3 验收时五工程累计 **319 项**通过，见 [验收记录](milestones/m7.md)；后续外部控制检查与当前计数见 [README](../README.md)。
