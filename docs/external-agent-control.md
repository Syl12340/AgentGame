# 外部 Agent 主动控制运行中的游戏

2026-10-04。`serve` 提供持久的单人游戏会话，外部 Agent 主动读取局部观测并提交动作。游戏端没有 LLM API 调用，不需要模型密钥；模型的选择、推理和探索记忆由外部 Agent 负责。HTTP 控制接口与 Python stdio MCP 桥接均已实现。

## 启动游戏

在项目根目录的真实终端运行：

```powershell
dotnet run --project src/AgentGame.Cli -c Release -- serve --scenario tests/Fixtures/Core/facility-small.json --port 8765 --tui
```

`--tui` 显示当前玩家的局部视图，动作由外部 Agent 提交；Ctrl+C 关闭服务。需要后台或重定向输出时去掉 `--tui`。可添加 `--record artifacts/external-demo.jsonl` 保存权威回放，文件必须尚不存在。随机地图可先用 `scenario generate` 生成，再将路径传给 `--scenario`。

服务仅绑定 `127.0.0.1`，默认端口 8765；`--port 0` 自动选择可用端口。启动成功后 stderr 写一行 `external-server/1` JSON，其中包含实际 `url`、`session_id`、`state` 和 `tick`。无 TUI 时 stdout 不输出游戏画面或协议日志。使用 .NET 10 SDK 随附的 ASP.NET Core/Kestrel，共享框架引用无需第三方 NuGet 包或管理员 URL ACL 初始化。

Agent 没有动作时游戏一直等待，查询和断开连接不会自动消耗回合；Agent 重新连接可以继续同一局。通关或达到回合上限后，服务仍保持运行供查询；新开一局需要重启 `serve`。本入口使用单席位 `scenario/1`；多人 CLI 与每席位外部控制入口仍待后续接入。多个客户端控制的是同一个角色。

## HTTP 契约

| 请求 | 返回/作用 |
|---|---|
| `GET /v1/session` | `external-session/1` 元数据：`session_id`、`state`、`tick`、`request_id`、`result`、`error` |
| `GET /v1/observation` | 当前 `agent/1` 局部 `observation`，终局后为 `episode_end` |
| `POST /v1/actions` | 提交一个严格的 `agent/1` action，返回提交后的新观测或终局消息 |

局部观测包含当前位置、当前可见格、库存、任务阶段与上次动作反馈，不包含全图、种子、核心哈希或参考解。Agent 自己记住已探索的区域。

先读观测，再原样携带其中的 `request_id` 提交动作。该编号包含会话随机标识与当前 tick；重启游戏后旧编号失效。

```powershell
$gameUrl = 'http://127.0.0.1:8765'
$observation = Invoke-RestMethod "$gameUrl/v1/observation"
$actionBody = @{
    type = 'action'
    request_id = $observation.request_id
    action = @{ type = 'move'; direction = 'east' }
} | ConvertTo-Json -Depth 8 -Compress
Invoke-RestMethod "$gameUrl/v1/actions" -Method Post -ContentType 'application/json; charset=utf-8' -Body $actionBody
```

动作：`move`、`interact` 必须带 `north/east/south/west` 方向；`pickup`、`wait` 必须省略方向。北方 `(0,-1)`、东方 `(1,0)`、南方 `(0,1)`、西方 `(-1,0)`。走到物品所在格再拾取；持门卡时朝相邻门交互；携核心回出口成功。被墙阻挡、无物可拾等合法动作仍消耗一回合。

动作正文为 UTF-8 单个 JSON 对象，最多 64 KiB，使用 `application/json`；复用原严格解析器，拒绝重复、多余、缺失字段、非法方向、无效 UTF-8 和多条消息。HTTP 请求不进行 `hello/ready` 握手。

| HTTP 状态 | 典型错误码 | 后续操作 |
|---|---|---|
| 400 | `invalid_action` | 修正动作格式；回合未推进 |
| 409 | `stale_request` | 重新读取观测，再决定动作 |
| 409 | `episode_ended` | 读取终局结果；游戏不再接收动作 |
| 413 / 415 | 请求过大 / `content_type` | 修正正文或 Content-Type；回合未推进 |
| 403 | `origin_rejected` | 使用直接的本地 Agent 客户端；不支持浏览器跨来源控制 |
| 503 | `record_error` / `session_failed` | 查询状态；该会话停止接受动作 |

错误正文为 `{"error":{"code":"...","detail":"..."}}`。所有请求响应标记 `Cache-Control: no-store`。未知路径 404，已知路径错误方法 405。

`state` 是会话执行状态，`result` 是已提交的规则结果，二者分别报告：例如最后一回合已经成功提交，但 footer 写入或文件释放失败时，状态为 `failed`、`error=record_error`、`tick` 为最终已提交回合，同时 `result.kind=success`。此时通过 `game_status` 查询结果，`game_observe` 不再返回可操作观测。已写完的档案能否核验与文件释放是否成功分别判断。

相同编号的并发/重复提交至多接受一次；其余返回 409。网络断开可能发生在动作已提交、响应未送达之后，客户端必须重新读状态/观测，不能自动重试旧动作。服务串行化读取、动作和关闭；记录写入成功后才发布新状态与新编号。记录失败时状态 tick 保持在最后提交边界，不暴露推进后但未记录的游戏状态。

## MCP 工具入口

`tools/game-mcp/server.py` 是 stdio MCP 服务，用 Python 3 标准库向上述 HTTP 会话转发。它不启动或结束游戏，不调用模型，不自动选择动作。它实现 [MCP 初始化生命周期](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)、[stdio 传输](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)和 [tools/list、tools/call](https://modelcontextprotocol.io/specification/2025-11-25/server/tools)；支持 2024-11-05、2025-03-26、2025-06-18、2025-11-25 的版本协商。

| 工具 | 参数 | 行为 |
|---|---|---|
| `game_status` | `{}` | 查询局状态 |
| `game_observe` | `{}` | 读取当前局部观测 |
| `game_act` | `request_id`、`type`，方向动作附带 `direction` | 提交动作并返回新观测 |

工具失败返回 `isError: true`；HTTP 的 `stale_request` 等代码保留在结果里。错误工具参数返回 JSON-RPC `-32602`。工具结果同时提供 JSON 文本 `content` 与对象 `structuredContent`。

在支持 stdio MCP 的 Agent 客户端中加入以下配置（配置文件位置由客户端决定；本项目不会修改全局配置）：

```json
{
  "mcpServers": {
    "facility-zero": {
      "command": "python",
      "args": [
        "D:/Shaoyilei/Desktop/Workspace/code/ai-agent-game1/tools/game-mcp/server.py",
        "--url", "http://127.0.0.1:8765"
      ]
    }
  }
}
```

先启动游戏，再连接 MCP。客户端需要启用这三个工具；可以给外部 Agent 如下任务：

> 使用 game_status、game_observe、game_act 玩当前 Facility Zero。通过局部观测建立自己的地图记忆，找门卡、打开门、拾取核心并返回出口。每次动作使用最新 request_id，move/interact 附带 north/east/south/west，pickup/wait 不带方向。观察反馈并调整计划，直到终局。过期编号或网络错误后先重新观察。

桥接的 `--url` 默认 `http://127.0.0.1:8765`，仅接受带端口的本地 HTTP 地址；`--timeout` 默认 10 秒且必须为正有限数。禁用代理、重定向和自动动作重试。stdin/stdout 只传 UTF-8 JSONL；EOF 正常退出，退出桥接不影响游戏。连接地址无需模型密钥。

## 回放与验收

正常规则终局写完 footer 并释放记录文件，可在游戏服务仍运行时使用原命令：

```powershell
dotnet run --project src/AgentGame.Cli -c Release -- verify artifacts/external-demo.jsonl
dotnet run --project src/AgentGame.Cli -c Release -- replay artifacts/external-demo.jsonl --tui
```

Ctrl+C 正常关闭未完成会话时记录 `aborted`；强制杀死进程可能留下 `incomplete`，现有 reader 保留可读前缀。结束后的查询不会添加动作或重复 footer。

实现位置：`src/AgentGame.Runtime/External/ExternalGameSession.cs`、`src/AgentGame.Cli/ExternalGameServer.cs`、`tools/game-mcp/server.py`。

```powershell
dotnet build AgentGame.slnx -c Release
dotnet run --project tests/AgentGame.Runtime.Tests -c Release --no-build -- --external-only
python tests/external_game_checks.py
```

独立验收：会话检查 10 项，真实 HTTP/MCP 检查 10 项；后者真实启动游戏进程与 MCP 进程，两个控制路径均完成 13 回合并核验记录，同时覆盖等待、局部视野、重新连接、同编号并发、非法动作、终局查询、协议初始化和传输失败。没有调用付费模型接口，也未修改 DSH/AGY 的全局 MCP 配置。终端渲染复用既有视图，真实屏幕观感仍待人工确认。
