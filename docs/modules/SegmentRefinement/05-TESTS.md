# 05-TESTS — SegmentRefinement 测试计划

## 单元测试

### GetSegmentsAsync 测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-01 | 文档存在且有 segments | valid documentId | 返回 segments 列表 + profile |
| T-02 | 文档不存在 | invalid documentId | 抛出 KeyNotFoundException |
| T-03 | 文档无 segments | documentId with 0 segments | 返回空列表 |
| T-04 | 文档有 llm_profile_json | document with profile | 返回 profile 对象 |
| T-05 | 文档无 llm_profile_json | document without profile | profile 为 null |

### RefineSegmentsAsync 测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-06 | 合并 2 条 segment | 2 sentenceIds + merge action | 备份创建 + LLM 调用 + 新 segments 写入 |
| T-07 | 拆分 1 条 segment | 1 sentenceId + split action | 备份创建 + 2 条新 segment |
| T-08 | 修改 segment 类型 | 1 sentenceId + retype action | segment_type 更新 |
| T-09 | 混合修正 | merge + split + retype | 所有修正正确应用 |
| T-09a | 拆分并双向合并 | 1 sentenceId + splitMerge + splitPosition + mergeFirstWithPrevious + mergeSecondWithNext | 前半与上一段合并，后半与下一段合并 |
| T-09b | 拆分并单侧合并 | 1 sentenceId + splitMerge + mergeFirstWithPrevious=true, mergeSecondWithNext=false | 前半与上一段合并，后半独立 |
| T-09c | 拆分并合并（无相邻段） | 1 sentenceId + splitMerge + 首段无前一段 | 前半独立，后半与下一段合并 |
| T-10 | 修正数量超限 (>20) | 21 corrections | 抛出验证异常 |
| T-11 | 文档不在 ready 状态 | processing document | 抛出验证异常 |
| T-12 | 单页 LLM 调用失败 | 某页 LLM returns error | 创建新实体复制原内容，其余页正常继续，整体不抛异常 |

### LLM Refinement 测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-13 | 构造 few-shot prompt | 3 corrections | prompt 包含 3 个 examples |
| T-14 | 解析 LLM 返回的 profile | valid JSON | DocumentProfile 对象 |
| T-15 | 解析 LLM 返回的 segments | valid JSON | SegmentResult 列表 |
| T-16 | LLM 返回格式异常 | invalid JSON | 重试后回退 |

### 备份/回滚测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-17 | 创建备份 | documentId + segments | backup 记录写入 DB |
| T-18 | 回滚成功 | backupId | segments 恢复到备份状态 |
| T-19 | 回滚后数据一致 | backupId | occurrences 也恢复 |

### DocumentProfile 持久化测试

| # | 测试场景 | 输入 | 预期输出 |
|---|---------|------|---------|
| T-20 | IngestionWorker 保存 profile | parsed document with profile | llm_profile_json 有值 |
| T-21 | IngestionWorker 无 profile | parsed document without profile | llm_profile_json 为 null |

## 集成测试

| # | 测试场景 | 验证点 |
|---|---------|-------|
| IT-01 | 完整修正流程 | 上传文档 → 解析 → 获取 segments → 提交修正 → 重新拆分 → 验证新 segments |
| IT-02 | 回滚流程 | 模拟 LLM 失败 → 验证数据恢复到备份状态 |
| IT-03 | 搜索索引同步 | 重新拆分后 → 搜索验证新 segments 可被检索 |

## 边界测试

| # | 测试场景 | 验证点 |
|---|---------|-------|
| BT-01 | 空 corrections 列表 | 返回 400 |
| BT-02 | 合并 1 条 segment（不足 2 条） | 返回 400 |
| BT-03 | 拆分位置超出文本长度 | 返回 400 |
| BT-04 | 拆分位置为 0 或文本末尾 | 返回 400 |
| BT-05 | 未知的 action 类型 | 返回 400 |
