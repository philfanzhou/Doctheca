# 01-FEATURE — SegmentRefinement 功能概述

## 功能名称

**SegmentRefinement** — 人工微调文档分段策略

## 功能概述

SegmentRefinement 是 DocumentParsing 模块的下游功能，提供一个人机协作（Human-in-the-Loop）界面，允许管理员查看 LLM 分析后的文档分段结果，手动修正不准确的分段，并将修正结果反馈给 LLM 作为学习样本，重新执行文档拆分入库。

核心价值：**通过少量人工微调，让 LLM 理解"正确应该怎么拆"，从而提升整个文档的分段质量。**

## 单一用户故事

> **作为** 管理员，
> **我希望** 在管理界面上查看 LLM 分析后的文档分段策略和每条 segment，能够合并、拆分、修改类型，并将修正结果发回 LLM 重新拆分入库，
> **以便** 通过少量人工标注让 LLM 学习正确的分段方式，提升文档检索的准确性。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | 文档解析完成后，LLM 分析结果（DocumentProfile）持久化到 documents 表的 llm_profile_json 字段 | 单元测试验证 IngestionWorker 保存 profile |
| AC-2 | 管理 API 返回文档的 LLM 分析结果（subject/docType/strategy/structure） | API 测试验证 GET /admin/documents/{id}/segments 响应包含 profile |
| AC-3 | 管理 API 返回文档的所有 segments，按 SentenceId 排序 | API 测试验证 segments 列表完整性 |
| AC-4 | 管理员可选中多条相邻 segment 执行合并，合并后文本拼接、offset 取首尾 | 单元测试验证合并逻辑 |
| AC-5 | 管理员可在 segment 文本中指定位置执行拆分，拆分为两条新 segment | 单元测试验证拆分逻辑 |
| AC-6 | 管理员可修改 segment 的类型（sentence/concept/word_entry/knowledge_point） | 单元测试验证类型更新 |
| AC-7 | 提交修正后，系统将修正的 segments 作为 few-shot examples 发给 LLM，重新分析整个文档 | 单元测试验证 LLM 收到 examples |
| AC-8 | 重新拆分前自动备份旧 segments 到备份表，失败时可回滚 | 单元测试验证备份和回滚 |
| AC-9 | 重新拆分后，旧 segments/occurrences 被删除，新数据写入，搜索索引同步更新 | 集成测试验证完整流程 |
| AC-10 | 前端界面展示 LLM 分析结果、segment 列表，支持合并/拆分/改类型操作 | 手动测试验证 UI 交互 |

## 范围内

- documents 表新增 llm_profile_json 字段
- Admin API：获取 segments、提交修正、重新拆分
- LLM few-shot refinement（基于用户修正样本重新分析）
- segment 备份与回滚机制
- 管理前端：segment 查看、合并、拆分、改类型、提交修正

## 范围外

- 自动分段质量评估（未来扩展）
- 多文档批量修正（当前仅支持单文档）
- 实时协作编辑（当前仅单人操作）
- 分段策略的 A/B 测试

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| DocumentEntity | `src/Database/Entities/DocumentEntity.cs` |
| DatabaseInitializer | `src/Database/DatabaseInitializer.cs` |
| DocumentAdminEndpoints | `src/Service/DocumentAdminEndpoints.cs` |
| IDocumentDomainService | `src/Domain/Services/IDocumentDomainService.cs` |
| ILlmSegmentationService | `src/Domain/Repositories/ILlmSegmentationService.cs` |
| LlmSegmentationService | `src/Service/LlmSegmentationService.cs` |
| IngestionWorker | `src/Service/IngestionWorker.cs` |
| 前端 API Client | `frontend/src/services/docApi.ts` |
| 前端页面 | `frontend/src/App.vue`（分段管理对话框） |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、API 设计、数据模型 |
| [03-DESIGN.md](./03-DESIGN.md) | 技术架构、接口签名、流程图 |
| [04-TASKS.md](./04-TASKS.md) | 任务列表、依赖关系 |
| [05-TESTS.md](./05-TESTS.md) | 测试计划、测试用例 |
