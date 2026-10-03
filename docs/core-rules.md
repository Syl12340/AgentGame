# Facility Zero Core 规则（facility-zero/1）

M0–M1 实现规范。主实现位于 `src/AgentGame.Core`，Core 只使用基础库，不引用 Protocol 或任何外围运行模块。

## 坐标、布局与数据所有权

左上角为 (0,0)，x 向右、y 向下；north/east/south/west 分别为 (0,−1)/(1,0)/(0,1)/(−1,0)。地形仅 floor/wall，按 y*width+x 保存；对象坐标与地形独立。单出口、门卡、门与核心；仅起点与出口可重叠。地形输入从 ASCII 行转为 ImmutableArray，不保留可修改的输入数组。

默认 512 回合、可见半径 3；为手工小地图允许 1–128 的宽高，半径 0–128，max_ticks 为正 Int32。Core 的 Scenario 只做结构校验；门卡被困和可绕门场景是 M2 完整校验的反例，M1 不声称能拒绝全部不可解布局。

Game 为单逻辑所有者使用。Capture 只包含不可变 Scenario、tick、当前坐标、三项持有/门状态、上次动作结果和终局结果；Observation/StepResult 使用 ImmutableArray 与不可变值。后续 Step 不改变旧快照或观测。View 的探索记忆、seq、动画、墙钟和进程状态都不在 Core。

## 动作、反馈与事件

| 动作 | 已接受时效果 | 无效果/阻挡原因 |
|---|---|---|
| move(direction) | 到相邻 floor 或已开启门格 | out_of_bounds、wall、closed_door（blocked） |
| pickup | 脚下门卡/核心进入库存并从地面移除 | nothing_to_pick_up（no_effect） |
| interact(direction) | 有门卡时开启相邻闭门；卡不消耗，门不再关闭 | no_door、door_already_open（no_effect）；missing_key（blocked） |
| wait | 不改位置/库存 | applied，无错误原因 |

每个合法领域动作消耗 1 回合，包括 blocked/no_effect。未知动作、方向非法、move/interact 无方向、pickup/wait 多出方向会被拒绝且不改状态。协议错误在 M3 由 Host 拒绝，此处不进行 JSON 解析。

固定执行顺序：检查已终局 → 检查 Action → 动作效果/领域事件 → tick+1 → 成功检查 → 未成功时检查回合上限 → 保存 last_result → 必要的阶段变化事件 → 必要的终局事件。

持有核心并位于出口才成功；最后允许回合成功优先于上限。取核心但未返程仅进入 return_to_exit。阶段从 find_key/open_door/find_core/return_to_exit/succeeded 派生。终局类型 success 表示 terminated，turn_limit 表示 truncated；Agent/记录错误不由 Core 表示。终局之后 Step 抛 InvalidOperationException，不推进状态。

事件是有类型的领域事实，不含纹理、JSON 序号、PID、耗时或 UI 资源。效果事件先于 MissionPhaseChanged，Succeeded/TurnLimitReached 最后。开门、pickup、等待、撞墙和无效交互分别有可解释事件。

## 可见性

当前格始终可见；候选格在 Chebyshev 半径内（含边界），按 y 再 x 顺序输出。对每个目标从格心到格心走整数 supercover 射线，穿过的墙和闭门阻断视线。

- 目标格自身为阻挡格时可见，阻挡格后面的格子不可见。
- 射线恰好穿过角点时，先检查两个相邻正交格；其中任一为墙/闭门/界外则挡住对角目标。
- 两个正交格都透明才进入对角格；不使用浮点斜率或随机误差。
- 开门立即影响下一次 Observe。未知格不返回为 floor，不泄露门后物品。

Observe 输出当前可见地形/对象/出口、当前坐标、库存、任务阶段、last_result 和终局结果。不输出完整 Scenario、seed、哈希、解路径或历史探索遮罩。门是否开启只随可见的门格返回；Agent 自行记忆过去。

## 手工验收场景

`tests/Fixtures/Core/facility-small.json` 为 9×3 直走廊：起点/出口 (1,1)，卡 (2,1)，门 (4,1)，核心 (6,1)。参考动作单独在 `.actions.json` 中保存，总计 13 回合；第 8 回合取得核心，第 13 回合返回成功。将 max_ticks 改为 13 应成功，12 应截断。

`visibility-corners.json` 为 7×7 遮挡地图；起点旁两个正交墙阻挡对角格。两张额外坏场景分别让卡在门后和存在绕门回路，M2 必须拒绝。JSON fixture 的文件 I/O 和解析仅在测试/外围进行，不进入 Core。

哈希规范及独立向量见 [core-state-format.md](core-state-format.md)。验收程序在 `tests/AgentGame.Core.Tests`，使用零额外依赖的 console 检查，失败返回非零；不能把 `dotnet test` 无测试输出当作通过。