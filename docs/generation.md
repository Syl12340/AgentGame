# M2 场景生成模块 (M2 Generation Module)

本文档介绍了 AgentGame M2 阶段场景生成、求解及验证模块的设计与实现。

## 1. 场景生成 (Scenario Generation)
- **固定四室拓扑 (Fixed Four-room Topology)**：场景划分为由一扇强制门连接的四个区域（房间），确保玩家在流程中必须开门。
- **对象放置 (Object Placement)**：通过逻辑校验分配起始点、钥匙、出口以及核心，保证关卡的基础可解性。
- **三个命名流 (Three Named Streams)**：使用三个相互独立的 PCG32 随机流（`map`、`objects`、`mission`）分离地形、物品与任务逻辑的生成，确保稳定可复现。
- **默认尺寸与重试界限 (Defaults & Bounded Retries)**：默认地图尺寸为 21x13。引入有界重试机制，单次生成默认最多尝试 16 次（default 16）。
- **最短参考路径限制 (Shortest Reference Limit)**：确保场景生成有效且生成的解题最短步数不超过限制（默认 128 步）。

## 2. 场景求解器 (Scenario Solver)
- **复用核心游戏逻辑**：求解器内部直接复用实际的 `Game.Step` 执行状态转移。
- **BFS 搜索策略**：使用广度优先搜索，且可忽略 `tick` 属性，因为到达同一核心状态的最早时刻 (earliest state dominates) 即为最优解。
- **状态空间界限**：状态空间被严格约束，最大探索状态数为 `8 * width * height`（由位置坐标、是否持钥匙、门开关状态、是否持核心计算得出：1 * 2 * 2 * 2 = 8）。
- **固定动作顺序 (Fixed Action Order)**：搜索过程遵循固定的操作方向与动作优先级顺序遍历分支，保证路线的确定性。

## 3. 三层验证机制

1. **结构验证**：Protocol 严格解析版本、字段、UTF-8、尺寸、对象位置和预算；Runtime 将通过检查的数据映射为 Core 场景。
2. **任务拓扑验证**：门关闭时起点能到达门卡和出口，不能到达核心；门打开后核心必须可达。
3. **完整任务求解**：通过真实 `Game.Step` 找到在场景 `max_ticks` 内完成拿卡、开门、取核心、返程的最短路线；若文件提供 `reference_length`，必须与实际长度一致。

128步是生成器的默认候选筛选上限，可配置；导入场景验证不额外施加128步限制，遵循文件的 `max_ticks`。

## 4. 命令行接口 (CLI)
- **生成与验证**：支持 `scenario generate` 与 `scenario validate`。
- **拒绝覆盖 (Refuse Overwrite)**：在生成命令中，系统严格拒绝覆盖现有文件。
- **退出与输出**：0表示成功、1表示场景/生成/I/O失败、2表示参数错误。生成与验证摘要写stdout；无效任务也返回 `valid=false` 的验证摘要。生成耗尽重试时stderr JSON包含 `error/seed/generator/generation_attempts`；解析和I/O失败返回stderr错误JSON，参数错误为stderr文本。

生成示例：`scenario generate --seed 42 --out new-scene.json`。验证：`scenario validate new-scene.json`。可选生成参数为 `--max-ticks`、`--max-reference-length`、`--max-attempts`。命令不输出内部参考动作。

## 5. 父级验证证据 (Parent Verification Evidence)
*声明：以下内容为父级验证证据，非本阶段自行测试的数据：*
- **种子成功率**：对种子范围 0..99 (seeds 0..99) 的生成进行验证，**全部成功生成**。
- **运行指标**：在所有验证的种子中，最长路径 (max route) 为 97 步，最大尝试次数 (max attempt) 为 1 次。
- **检查总计**：父级共计执行了 103 项检查，涵盖 Core 39 项、Protocol 22 项、Runtime 10 项、Architecture 12 项以及 CLI 20 项。
