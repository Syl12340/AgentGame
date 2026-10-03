# Scenario 格式（scenario/1）

M0 冻结。JSON 文件/单行 JSONL 均可。字段集合与 `tests/Fixtures/Core/facility-small.json` 一致；Protocol 侧的 `ScenarioCodec` 是独立的结构读取器（非 Core 校验器），只冻结 wire 契约。

## 字段

`snake_case` 固定：`format`、`rules`、`generator`、`rows`、`start`、`exit`、`key`、`door`、`core`、`max_ticks`、`visibility_radius`。`format=scenario/1`，`rules=facility-zero/1`，`generator` 为生成器元数据（样例 `manual/1`）。

## 常量与几何

- 左上角为 (0,0)，x 向右、y 向下。
- `rows` 为字符串数组，仅 ASCII `#`（墙）与 `.`（地板）；宽高在 1–128（Core 允许手工小地图）。所有行等宽（矩形）。
- `start`、`exit`、`key`、`door`、`core` 为 `{x,y}` 点，须在地图内且位于地板格。
- 仅允许 `start` 与 `exit` 重叠；`exit/key/door/core` 互不重叠，`start` 不与其他非出口对象重叠。
- `max_ticks>=1`；`visibility_radius` 在 0–128。

## 示例（与 Core fixture 一致）

```json
{"format":"scenario/1","rules":"facility-zero/1","generator":"manual/1",
 "rows":["#########","#.......#","#########"],
 "start":{"x":1,"y":1},"exit":{"x":1,"y":1},"key":{"x":2,"y":1},"door":{"x":4,"y":1},"core":{"x":6,"y":1},
 "max_ticks":512,"visibility_radius":3}
```

## 严格解析（ScenarioCodec）

拒绝：未知/缺失/多余/重复字段；`format`/`rules` 版本不符；`generator` 为空；`rows` 含非 `#`/`.` 字符、不等宽或宽度/高度越界；点越界或落在墙格；对象重叠；`max_ticks`/`visibility_radius` 越界；非单个 JSON 对象、额外 JSON、深度>32、超 64 KiB、无效 UTF-8。

`format`、`rules` 与字段版本相互独立：`scenario/1` 不代表规则等价。

## DTO 与实现

`ScenarioDto`（编码/解码对象），`ScenarioCodec.Parse`/`Encode`。
fixtures：`tests/Fixtures/Protocol/scenario/`。
Core 的完整任务求解校验已在 M2 实现（`scenario validate`）；Protocol 仍只负责结构边界。
## 生成元数据（M2 使用，Agent 不接收）

可选 `seed` 为规范 UInt64 十进制字符串（非 JSON 数字，无多余前导零），`generation_attempts` 为正 Int32，`reference_length` 为 1..max_ticks 的参考路径长度。手工场景可省略。它们不改变保存布局的 Core 哈希。M2 Runtime 重新求解布局，要求提供的 `reference_length` 等于最短完整任务长度，否则返回 `reference_length_mismatch`。

## M2 文件与任务验证

Runtime 以最多64 KiB+1字节的有界读取交给严格解析器，然后验证门关闭时起点可达门卡与出口、不可达核心，门打开后可达核心，最后通过真实 `Game.Step` 的完整状态求解。文件读取不信任生成器名称或 seed 作为可解证明。

`scenario generate --seed 42 --out <新文件>` 默认生成21×13地图，成功后写入 UTF-8 无BOM文件；拒绝覆盖。`scenario validate <文件>` 返回 JSON 验证摘要，包括实际参考长度与初始状态哈希，不输出参考动作。验证报告写stdout（无效场景也有 `valid=false` 报告）；解析、I/O或生成异常写stderr。退出码0成功、1任务/文件失败、2参数错误。
