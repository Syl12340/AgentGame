# M3 Agent 会话与执行结果

日期：2026-10-03。Agent接口保持 `agent/1`；CLI运行摘要版本 `run/1`。本文只描述 M3 的单局会话与摘要；Observer、权威记录、回放与核验已在 M4 交付，见 [observer-runtime.md](observer-runtime.md)。

## 使用

```powershell
dotnet run --project src/AgentGame.Cli -c Release -- run --scenario tests/Fixtures/Core/facility-small.json -- python -u agents/random_agent.py --seed 42
```

`--`前仅接受宿主选项 `--scenario`、`--handshake-ms`、`--decision-ms`；后面是可执行程序和独立参数。Runtime使用 `ProcessStartInfo.ArgumentList`，不经shell拼接。带空格或shell元字符的参数保持原值。

输入场景先冻结DTO数组，执行结构、任务拓扑及真实Core求解验证；无效任务或伪造参考长度在启动进程前拒绝。默认握手5秒、决策30秒、正常退出宽限1秒、强制终止等待3秒。

## 单局所有权

`GameRunner.RunAsync`是唯一Core所有者，按hello/ready、observation/action、episode_end顺序执行；同一时间只有一个未完成请求。请求ID为 `r<当前tick>`，初次为 `r0`。当前动作严格解析且ID匹配后，检查取消，再同步调用 `Game.Step`，中间不await。stderr读取任务从不访问Core。

Agent仅收到当前局部可见格、位置、库存、阶段、上次反馈和结束结果；没有场景布局、生成seed、Core哈希、内部求解路线或Viewer记忆。外部固定成功路线只存在于故障测试fixture，生产随机Agent只读stdin。

## 字节与期限

JSONL传输以固定4 KiB读取缓存及最多64 KiB+1字节的行缓存读取原始字节，LF/CRLF终止符不计入内容长度。超限在完整行形成前拒绝；EOF半行是 `incomplete_line`，空行由严格解析器拒绝。UTF-8、BOM、JSON深度32、字段和枚举仍由Protocol校验。

每次握手或决策的发送、flush和完整响应读取共享一个CancellationToken期限。宿主发出的消息也检查64 KiB上限；大地图/大视野不能无界发送观测。观察生成是同步规则计算，不计入Agent发送/响应期限。

重复/提前响应的已缓冲字节在接受动作前拒绝；稍后到达的额外响应在下一边界按类型/请求ID拒绝。已经合法提交的动作不会因随后检测到故障被回滚。结束后的额外stdout也被判为协议错误。

stderr持续按原始字节排空，环形尾部保留64 KiB，解码时替换坏UTF-8；它始终是诊断数据。尾部序列化为摘要中的 `stderr_tail`，不会作为动作或直接混入宿主stdout。

## 结束与清理

规则结束后发送episode_end并关闭stdin，等待正常退出；超过宽限则调用进程树终止，并等待根进程退出。错误或取消时直接进入终止清理。stdout/stderr读取等待也有界，不被继承管道句柄永久阻塞。

当前Windows验收逐例确认Agent根PID已退出。`.NET`的 `WaitForExit`/`HasExited`不能单独证明所有后代退出，因此完整派生进程/孤儿进程与Linux清理仍为M6验收条件，见 [Microsoft Process.Kill 文档](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)。取消等待本身不终止进程，Runtime会显式清理，见 [WaitForExitAsync 文档](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexitasync?view=net-10.0)。

## run/1 摘要

CLI只输出一行JSON；可选null字段省略。关键字段：

| 字段 | 含义 |
|---|---|
| format / kind | `run/1`；`success`、`turn_limit`、`execution_error`或`cancelled` |
| tick / core_hash | 最后有效Core状态的回合与规范哈希 |
| last_request_id / last_action | 最后真正进入Core的动作及其请求ID；无有效动作时省略 |
| episode / terminated / truncated | 规则终局及其标志；取消或进程错误不伪造规则终局 |
| error | code、phase、detail和可选失败请求ID；与领域反馈独立 |
| agent_name / agent_process_id / agent_exit_code | 握手名称、根PID及可取得的退出码 |
| forced_termination / stderr_tail | 是否强制结束与有界诊断尾部 |

规则已终止后若Agent异常退出，kind仍可为execution_error，同时保留episode/标志，明确区分任务结果与执行故障。正常规则成功或回合截断退出码0，执行错误1，参数错误2，取消130。输入场景/文件错误为stderr JSON，运行结果（含执行错误）为stdout JSON。

错误码包括 `start_failed`、`handshake_timeout`、`decision_timeout`、`protocol_violation`、`line_too_long`、`incomplete_line`、`agent_exited`、`transport_error`、`host_message_too_large`、`cancelled`及清理错误；错误不转换为wait，也不增加tick。
