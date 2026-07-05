# 01-FEATURE — DocumentParsing 功能概述

> **链路定位（2026-07 更新）**：本模块描述的是 **LLM 拆段链路**（`IngestionWorker` 驱动，写入 `documents` / `document_segments` / `question_segments`），为历史保留链路。OpenSearch 索引源已切换到 **MinerU 解析链路**（`MinerUFileParseWorker` 驱动，写入 `document_parse_blocks`），详见 [OpenSearchBlockIndexing](../OpenSearchBlockIndexing/01-FEATURE.md)。本模块的 `IndexDocumentSegmentsAsync` 调用仍保留但新代码不再调用（`ISearchIndexService` 已标注 legacy）。

## 功能名称

**DocumentParsing** — 文档解析与结构化处理

## 功能概述

DocumentParsing 是文档检索服务的核心后台处理模块，负责将用户上传的原始文档（PDF/DOCX/PPTX）自动解析为结构化数据，并写入数据库和搜索索引，使文档可被精确检索。

整个流程由 `IngestionWorker`（后台 BackgroundService）驱动：每 5 秒轮询待处理的导入任务，对每个任务执行 **下载 → 解析 → 结构化写入 → 索引同步** 的完整管线。

## 单一用户故事

> **作为** 后台工作器（IngestionWorker），
> **我希望** 每 5 秒轮询待处理的导入任务，对每个任务从 OSS 下载文件、调用解析器、将页面/片段/题目/Token 写入数据库，完成后同步写入搜索索引，
> **以便** 用户上传的文档能够被自动结构化处理并支持后续的精确检索。

## 验收条件

| # | 验收条件 | 验证方式 |
|---|---------|---------|
| AC-1 | Worker 每 5 秒轮询一次 pending 状态的导入任务 | 单元测试验证 `_pollInterval = TimeSpan.FromSeconds(5)`；集成测试观察轮询间隔 |
| AC-2 | 每个任务在独立的 try/catch 中处理，单个任务失败不影响其他任务 | 单元测试模拟第二个任务抛异常，验证第一个任务正常完成 |
| AC-3 | 解析流程按序执行：下载 OSS → ParseAsync → 写入 pages → 写入 segments → 写入 questions → 写入 occurrences → CompleteIngestionJobAsync | 集成测试验证数据库写入顺序和完整性 |
| AC-4 | PageNumber → PageId 映射正确，segment 和 question 的 PageId 字段指向正确的 page 记录 | 单元测试验证映射字典构建逻辑 |
| AC-5 | SentenceId → SegmentId 映射正确，occurrence 的 SegmentId 指向正确的 segment 记录；QuestionId → QuestionSegmentId 映射正确 | 单元测试验证 occurrence 中 SegmentId/QuestionSegmentId 的正确性 |
| AC-6 | 解析完成后同步写入搜索索引（ISearchIndexService.IndexDocumentSegmentsAsync），失败仅记日志不影响任务状态 | 单元测试模拟 IndexDocumentSegmentsAsync 抛异常，验证任务仍为 success |
| AC-7 | 解析失败时标记任务 failed + 文档 failed + 记录 ErrorMessage | 单元测试模拟 ParseAsync 抛异常，验证 FailIngestionJobAsync 被调用 |
| AC-8 | Worker 使用 IServiceProvider.CreateScope 获取 Scoped 服务，避免生命周期问题 | 代码审查验证 `scope.ServiceProvider.GetRequiredService<T>()` 模式 |
| AC-9 | LLM 失败时不产生覆盖整 chunk 的单条 segment；非 sentence 策略的 LLM 失败触发任务级失败 | 单元测试 `ParseAsync_WordEntryLlmFails_ThrowsAndDoesNotCreateBigRecord`、`ParseAsync_SentenceLlmFails_FallsBackToRuleBased` |
| AC-10 | IngestionWorker 在 8 个关键阶段持久化 progress 到 job 实体，弹窗每 2 秒轮询展示 | 单元测试 `ExecuteAsync_ProgressTransitionsThroughAllStages`；端到端上传大文件时弹窗进度条按 5→15→45→60→80→95→100 顺序推进 |

## 范围内

- IngestionWorker 后台轮询与任务编排
- DocumentDomainService 任务状态管理（Start/Complete/Fail）
- IDocumentParserService 解析器接口与 DocumentParserService 实现
- ParsedDocument → 数据库模型转换与写入（pages/segments/questions/occurrences）
- PageNumber → PageId、SentenceId → SegmentId、QuestionId → QuestionSegmentId 映射
- 解析完成后同步写入 OpenSearch 搜索索引
- 解析失败时的错误处理与状态标记

## 范围外

- 文档上传与 OSS 存储管理（由 DocumentAdminEndpoints 处理）
- 搜索查询功能（由 SearchDomainService 处理）
- 文档元数据管理（由 DocumentDomainService.UpdateMetadataAsync 处理）
- 文档删除与索引清理（由 DocumentDomainService.DeleteDocumentAsync 处理）
- OCR 引擎集成（当前仅 OCR 后处理，不包含 OCR 识别能力）
- 任务优先级与调度策略

## 关键代码参考

| 组件 | 文件路径 |
|------|---------|
| IngestionWorker | `src/Service/IngestionWorker.cs` |
| DocumentDomainService | `src/Domain/Services/DocumentDomainService.cs` |
| IDocumentParserService | `src/Domain/Repositories/IDocumentParserService.cs` |
| DocumentParserService | `src/Service/DocumentParserService.cs` |
| ParsedDocument 模型 | `src/Domain/Models/DocumentModels.cs` |
| ISearchIndexService | `src/Domain/Repositories/ISearchIndexService.cs` |
| OpenSearchIndexService | `src/Service/OpenSearchIndexService.cs` |
| 数据库实体 | `src/Database/Entities/` |
| 仓储接口 | `src/Domain/Repositories/IRepositories.cs` |

## 文档索引

| 文档 | 说明 |
|------|------|
| [01-FEATURE.md](./01-FEATURE.md) | 功能概述、用户故事、验收条件（本文档） |
| [02-SPEC.md](./02-SPEC.md) | 需求规格、验收场景、非功能需求、测试策略 |
| [03-DESIGN.md](./03-DESIGN.md) | 文件结构、接口签名、解析流程图、错误处理、外部依赖 |
| [04-TASKS.md](./04-TASKS.md) | JSON 任务列表、命令速查、依赖图 |
| [05-TESTS.md](./05-TESTS.md) | 单元/集成/边界测试表、骨架代码 |
| [06-CONVENTIONS.md](./06-CONVENTIONS.md) | 命名、日志、错误消息、代码风格 |
| [07-DOCUMENT-ANALYSIS.md](./07-DOCUMENT-ANALYSIS.md) | LLM 智能文档分析设计（替代规则切割） |
