# AGY 子代理空答案排查

日期：2026-10-03。模型 `gemini-3.1-pro-high`。初始CLI为1.2.14，最终新MCP实例报告1.2.16；本任务没有执行CLI升级。

## 根因与证据

默认无交互模式遇到 `write_file` 权限请求时无法询问用户，自动拒绝操作，却返回退出码0与JSON `status=SUCCESS`、空 `response`。结构化输出含 `denied_actions:[{"action":"write_file","display_name":"WriteToFile"}]`，stderr明确说明headless权限被拒绝。

旧桥接仅凭退出码0判断成功，丢弃这时的stderr，并将空文本转换为“成功但空答案”。因此模型连接正常也可能没有任何文件交付。

对照测试：简单回答和只读README任务成功；默认模式最小写入被拒绝且没有生成目标文件；保持sandbox并显式使用 `--mode accept-edits` 后，目标文件内容确实为 `AGY_WRITE_OK`。`--print=...` 与分离参数形式都能正常回答，不是本次根因。

## 已应用修复

安装文件：`C:\Users\Shaoyilei\.codex\antigravity-bridge\server.mjs`，桥接版本1.0.2。

- `allow_mutation=true`时使用 `--mode accept-edits`；false时使用 `--mode plan`。保留sandbox、工作目录及禁止无关操作的任务约束。
- 使用JSON输出，验证任务状态和非空回答；`denied_actions`非空即报告错误，不能被部分回答掩盖。
- CLI空输出也作为错误；health明确只证明CLI可执行，不证明模型任务可交付。
- 未启用 `--dangerously-skip-permissions`，未修改账号、凭据或全局权限允许规则。

原始桥接已备份为 `server.mjs.pre-json-guard-20261003.bak`，与安装文件在同一目录。原始SHA-256：`352EDAC307CB0896E7BDED89492E507B2351BBA868D68D1CC8CD1AD0CEF8DF9E`。

## 修复验证

7项响应解析检查通过，覆盖成功、空答案、失败状态、坏JSON、缺失字段、被拒绝工具。新启动的MCP服务器实际完成：

1. `allow_mutation=false`读取README并正确返回首标题；
2. `allow_mutation=true`写入 `docs/generation.md` 并返回非空结果，主代理已检查文件及内容。

诊断脚本 `scripts/probe-agy.ps1` 与本地证据 `artifacts/agy-diagnostics/` 保留。修复测试客户端使用新MCP进程，未中止桌面应用的既有连接。当前聊天连接的旧进程仍报告原版health文本；重新连接MCP或重启Codex后加载已修复文件。无需重新安装.NET或模型。

M3补充：`accept-edits`允许获准的文件修改，但sandbox的终端 `command` 仍可能在headless模式被拒绝。桥接已按 `denied_actions` 明确报告该情况。限定任务使用内置文件工具后，AGY实际交付 `JsonLineTransport.cs`；没有为方便构建开启全工具自动许可，构建和验收由主代理执行。
