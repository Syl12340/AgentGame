# Core 规范状态编码（core-state/1）

对 Capture 的快照编码，不使用 JSON 属性顺序或对象 GetHashCode。输出 SHA-256 小写十六进制。规则版本独立为 facility-zero/1。

所有 Int32/Int64 都为 little-endian 有符号固定宽度；byte 为单字节；bool 编码 0/1；字符串为 Int32 UTF-8 字节长度随后是内容，不使用 BOM/NUL。坐标为 x:Int32 然后 y:Int32。

| 顺序 | 内容 |
|---:|---|
| 1 | 编码版本字符串 core-state/1 |
| 2 | 规则版本字符串 facility-zero/1 |
| 3 | width、height、max_ticks、visibility_radius：各 Int32 |
| 4 | start、exit、key、door、core：各 (x,y) |
| 5 | 地形格数 Int32；按 y-major 每格一 byte：floor=0，wall=1 |
| 6 | tick:Int64；当前 player (x,y) |
| 7 | has_key、door_open、has_core：各 byte |
| 8 | last_result：无=0、applied=1、blocked=2、no_effect=3（byte）；reason 字符串，无理由为空 |
| 9 | episode：未结束=0、success=1、turn_limit=2（byte） |

任务阶段和终止标志可由现有字段唯一派生，不重复编码。last_result 属于可观察的 Core 状态，纳入编码。初始布局和所有影响规则/观测的参数均被包含；生成版本/seed 等不影响已保存布局的元数据不在此编码中。

规则事件不放进状态字节，重演时应单独比较其顺序和值。Observer seq/run_id、动画、真实耗时、stderr、外部 Agent 请求与 Viewer 的探索记忆不进入 Core 哈希。

独立向量在 `tests/Fixtures/Core/core-golden.json`，由 `generate_vectors.py` 使用 Python struct 直接按本表编码，完全不调用 C# 实现。初始和第 13 回合成功的完整 hex 与 SHA-256 作为固定验收输入，不在测试运行时重新生成期望值。以后格式变化应升版本并增加新的向量，不能仅修改旧文件让测试变绿。

完整 CoreSnapshot 与受限 ObserverSnapshot 不是同一个契约。M1 只实现 Capture 和编码，尚未提供 checkpoint 文件或恢复运行命令。