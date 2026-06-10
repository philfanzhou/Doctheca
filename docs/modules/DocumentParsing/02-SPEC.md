# 02-SPEC — DocumentParsing 需求规格

## 需求列表

### REQ-PARSE-01：后台轮询机制

**优先级**: P0 | **状态**: 已实现

IngestionWorker 作为 BackgroundService，以 5 秒为间隔持续轮询 `status = "pending"` 的导入任务。

- 轮询间隔：`_pollInterval = TimeSpan.FromSeconds(5)`
- 查询方式：`DocumentDomainService.GetPendingJobsAsync()` → `IJobRepository.GetByStatusAsync("pending")`
- 取消支持：每轮循环检查 `stoppingToken.IsCancellationRequested`，每个任务处理前检查 `stoppingToken.ThrowIfCancellationRequested()`
- 作用域管理：每轮循环使用 `IServiceProvider.CreateScope()` 获取 Scoped 服务实例

**验收场景**:
```gherkin
Scenario: Worker 启动后持续轮询
  Given IngestionWorker 已启动
  When 数据库中有 pending 状态的任务
  Then Worker 在 5 秒内获取到该任务并开始处理

Scenario: 取消令牌触发时 Worker 优雅退出
  Given IngestionWorker 正在运行
  When stoppingToken 被取消
  Then Worker 退出循环，不再处理新任务
```

---

### REQ-PARSE-02：任务状态转换

**优先级**: P0 | **状态**: 已实现

导入任务和文档的状态在解析过程中同步转换：

| 阶段 | Job Status | Document Status | 触发方法 |
|------|-----------|----------------|---------|
| 创建 | pending | pending | CreateDocumentAsync |
| 开始处理 | processing | processing | StartIngestionJobAsync |
| 处理成功 | success | ready | CompleteIngestionJobAsync |
| 处理失败 | failed | failed | FailIngestionJobAsync |

状态转换细节：
- `StartIngestionJobAsync(jobId, parserVersion, ocrVersion)`：设置 `job.Status = "processing"`、`job.StartedAt`、`job.ParserVersion`、`job.OcrVersion`；同时设置 `document.Status = "processing"`
- `CompleteIngestionJobAsync(jobId)`：设置 `job.Status = "success"`、`job.FinishedAt`；同时设置 `document.Status = "ready"`
- `FailIngestionJobAsync(jobId, errorMessage)`：设置 `job.Status = "failed"`、`job.ErrorMessage`、`job.FinishedAt`；同时设置 `document.Status = "failed"`

**验收场景**:
```gherkin
Scenario: 任务成功完成
  Given 存在一个 pending 任务
  When 解析流程正常完成
  Then job.Status = "success" 且 document.Status = "ready"

Scenario: 任务处理失败
  Given 存在一个 pending 任务
  When 解析流程抛出异常
  Then job.Status = "failed" 且 document.Status = "failed" 且 job.ErrorMessage 记录异常消息
```

---

### REQ-PARSE-03：文件下载与解析

**优先级**: P0 | **状态**: 已实现

从 OSS 下载文件流，调用 IDocumentParserService.ParseAsync 进行解析。

- 下载：`IOssService.DownloadAsync(document.FilePath)` 获取文件流
- 解析：`IDocumentParserService.ParseAsync(fileStream, document.SourceType, stoppingToken)` 返回 `ParsedDocument`
- 支持的文件类型：`pdf`、`docx`/`doc`、`pptx`/`ppt`
- 不支持的类型抛出 `NotSupportedException`

**验收场景**:
```gherkin
Scenario: PDF 文件解析
  Given 文档 SourceType = "pdf"
  When 调用 ParseAsync
  Then 返回 ParsedDocument，Pages 包含按页解析的结果

Scenario: 不支持的文件类型
  Given 文档 SourceType = "xlsx"
  When 调用 ParseAsync
  Then 抛出 NotSupportedException，消息为 "不支持的文件类型：xlsx"
```

---

### REQ-PARSE-04：结构化数据写入 — Pages

**优先级**: P0 | **状态**: 已实现

将 ParsedDocument.Pages 转换为 DocumentPageModel 并批量写入数据库。

- 每个 ParsedPage 生成一个 DocumentPageModel
- 字段映射：`PageNumber` → `PageNumber`，`DocumentId` = `document.Id`
- 批量写入：`IPageRepository.AddRangeAsync(pageModels)`
- 映射构建：`pageLookup = pageModels.ToDictionary(p => p.PageNumber, p => p.Id)`

**验收场景**:
```gherkin
Scenario: 3 页 PDF 生成 3 条 page 记录
  Given ParsedDocument 包含 3 个 ParsedPage（PageNumber = 1, 2, 3）
  When 写入 pages
  Then 数据库新增 3 条 DocumentPageModel，PageNumber 分别为 1, 2, 3
  And pageLookup[1], pageLookup[2], pageLookup[3] 分别为对应的 Guid
```

---

### REQ-PARSE-05：结构化数据写入 — Segments 和 Questions

**优先级**: P0 | **状态**: 已实现

将 ParsedPage.Segments 和 ParsedPage.Questions 转换为数据库模型并写入。

**Segments**:
- 遍历所有页的所有 segment，生成 DocumentSegmentModel
- `PageId` 通过 `pageLookup[page.PageNumber]` 获取
- 字段映射：`BlockId`、`SentenceId`、`SegmentType`、`Text`、`StartOffset`、`EndOffset`
- 批量写入：`ISegmentRepository.AddRangeAsync(segmentModels)`
- 映射构建：`segmentLookup = segmentModels.ToDictionary(s => s.SentenceId, s => s.Id)`

**Questions**:
- 遍历所有页的所有 question，生成 QuestionSegmentModel
- `PageId` 通过 `pageLookup[page.PageNumber]` 获取
- 字段映射：`QuestionId`、`Stem`、`OptionsJson`、`AnswerArea`、`StartOffset`、`EndOffset`
- 批量写入：`IQuestionRepository.AddRangeAsync(questionModels)`

**验收场景**:
```gherkin
Scenario: Segment 的 PageId 正确映射
  Given pageLookup[1] = Guid-A
  And ParsedPage(PageNumber=1) 包含 2 个 segment
  When 写入 segments
  Then 2 个 DocumentSegmentModel 的 PageId 均为 Guid-A

Scenario: Question 的 PageId 正确映射
  Given pageLookup[2] = Guid-B
  And ParsedPage(PageNumber=2) 包含 1 个 question
  When 写入 questions
  Then QuestionSegmentModel 的 PageId 为 Guid-B
```

---

### REQ-PARSE-06：结构化数据写入 — Occurrences（Token）

**优先级**: P0 | **状态**: 已实现

将 segment 和 question 中的 Token 转换为 DocumentOccurrenceModel 并写入。

- Segment Token：`SegmentId` = `segmentLookup[seg.SentenceId]`，`QuestionSegmentId` = null
- Question Token：`SegmentId` = null，`QuestionSegmentId` = `questionLookup[question.QuestionId]`
- questionLookup 按页构建：`questionModels.Where(qm => page.Questions.Any(pq => pq.QuestionId == qm.QuestionId)).ToDictionary(qm => qm.QuestionId, qm => qm.Id)`
- 字段映射：`TokenText`、`TokenStem`、`StartOffset`、`EndOffset`
- 仅在 `occurrenceModels.Count > 0` 时写入

**验收场景**:
```gherkin
Scenario: Segment Token 的 SegmentId 正确映射
  Given segmentLookup["p1-b1-s1"] = Guid-S1
  And ParsedSegment(SentenceId="p1-b1-s1") 包含 3 个 Token
  When 写入 occurrences
  Then 3 个 DocumentOccurrenceModel 的 SegmentId = Guid-S1，QuestionSegmentId = null

Scenario: Question Token 的 QuestionSegmentId 正确映射
  Given questionLookup["q1"] = Guid-Q1
  And ParsedQuestion(QuestionId="q1") 包含 2 个 Token
  When 写入 occurrences
  Then 2 个 DocumentOccurrenceModel 的 QuestionSegmentId = Guid-Q1，SegmentId = null

Scenario: 无 Token 时不写入
  Given ParsedDocument 所有 segment 和 question 的 Tokens 均为空列表
  When 处理 occurrences
  Then 不调用 occurrenceRepository.AddRangeAsync
```

---

### REQ-PARSE-07：搜索索引同步

**优先级**: P1 | **状态**: 已实现

解析完成后，同步调用 `ISearchIndexService.IndexDocumentSegmentsAsync` 将文档 segments 写入 OpenSearch。

- 调用条件：`_searchIndexService != null`
- 调用参数：`document.Id, document.Title, document.Subject, document.Grade, document.Year`
- 失败策略：try/catch 包裹，失败仅记日志（`_logger.LogError`），不影响任务 success 状态
- OpenSearch 实现内部：从数据库读取 segments 和 questions，构建 bulk 请求写入索引

**验收场景**:
```gherkin
Scenario: 搜索索引写入成功
  Given _searchIndexService 不为 null
  And 解析流程正常完成
  When 调用 IndexDocumentSegmentsAsync
  Then 日志输出 "文档搜索索引已创建：{DocumentId}"

Scenario: 搜索索引写入失败不影响任务状态
  Given _searchIndexService 不为 null
  And IndexDocumentSegmentsAsync 抛出异常
  When 解析流程完成
  Then job.Status 仍为 "success"，document.Status 仍为 "ready"
  And 日志输出 "创建文档搜索索引失败：{DocumentId}"
```

---

### REQ-PARSE-08：向量索引同步

**优先级**: P1 | **状态**: 已实现

解析完成后，同步调用 `IQdrantService.IndexDocumentVectorsAsync` 将文档向量写入 Qdrant。

- 调用条件：`_qdrantService != null`
- 调用参数：`document.Id, document.Title, document.Subject, document.Grade, document.Year`
- 失败策略：try/catch 包裹，失败仅记日志，不影响任务 success 状态
- Qdrant 实现内部：从数据库读取 segments 和 questions，调用 Embedding API 获取向量，批量 upsert

**验收场景**:
```gherkin
Scenario: 向量索引写入成功
  Given _qdrantService 不为 null
  And 解析流程正常完成
  When 调用 IndexDocumentVectorsAsync
  Then 日志输出 "文档向量索引已创建：{DocumentId}"

Scenario: 向量索引写入失败不影响任务状态
  Given _qdrantService 不为 null
  And IndexDocumentVectorsAsync 抛出异常
  When 解析流程完成
  Then job.Status 仍为 "success"，document.Status 仍为 "ready"
  And 日志输出 "创建文档向量索引失败：{DocumentId}"
```

---

### REQ-PARSE-09：错误处理与故障恢复

**优先级**: P0 | **状态**: 已实现

解析过程中任何异常均需妥善处理，确保系统稳定性。

- **任务级异常**：每个任务在独立 try/catch 中处理，捕获 Exception 后调用 `FailIngestionJobAsync(job.Id, ex.Message)`
- **FailIngestionJobAsync 自身异常**：嵌套 try/catch，记录日志 `"标记导入任务失败时出错：{JobId}"`
- **轮询级异常**：外层 try/catch 捕获轮询过程中的异常，记录日志 `"导入工作器轮询出错"`，Worker 继续运行
- **索引写入异常**：搜索索引和向量索引写入失败仅记日志，不标记任务失败

**验收场景**:
```gherkin
Scenario: 单个任务失败不影响后续任务
  Given 数据库中有 2 个 pending 任务
  When 第 1 个任务解析失败，第 2 个任务正常
  Then 第 1 个任务 status = "failed"，第 2 个任务 status = "success"

Scenario: FailIngestionJobAsync 失败时记录日志
  Given 任务解析失败
  And FailIngestionJobAsync 也抛出异常
  Then 记录 "标记导入任务失败时出错" 日志
  And Worker 继续运行
```

---

## 非功能需求

| 类别 | 需求 | 指标 |
|------|------|------|
| 可靠性 | 单个任务失败不影响其他任务 | 异常隔离 |
| 可靠性 | 索引写入失败不影响解析结果 | 解析与索引解耦 |
| 可观测性 | 关键步骤均有日志输出 | Information/Error 级别 |
| 可观测性 | 失败任务记录 ErrorMessage | 可追溯 |
| 可扩展性 | 解析器通过接口注入，可替换 | IDocumentParserService |
| 可扩展性 | 搜索索引和向量索引为可选依赖 | nullable 注入 |
| 性能 | 批量写入数据库（AddRangeAsync） | 减少 DB 往返 |
| 安全性 | Worker 使用 Scoped 服务 | 避免生命周期问题 |

## 测试策略

| 测试类型 | 覆盖范围 | 工具 |
|---------|---------|------|
| 单元测试 | IngestionWorker 编排逻辑、映射逻辑、错误处理 | xUnit + Moq |
| 单元测试 | DocumentDomainService 任务状态管理 | xUnit + Moq |
| 单元测试 | DocumentParserService 解析逻辑 | xUnit |
| 集成测试 | 完整解析流程（下载→解析→写入→索引） | xUnit + Testcontainers |
| 边界测试 | 空文档、无 Token、不支持的类型 | xUnit |
| 回归测试 | 映射正确性验证 | xUnit |

### 测试优先级

1. **P0 — 必须通过**：任务状态转换、映射正确性、错误隔离
2. **P1 — 应该通过**：索引同步、取消令牌处理
3. **P2 — 建议通过**：性能指标、并发场景
