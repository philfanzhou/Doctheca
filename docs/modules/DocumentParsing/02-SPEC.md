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
- 字段映射：`SentenceId`、`SegmentType`、`Text`、`StartOffset`、`EndOffset`
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
  Given segmentLookup["p1-s1"] = Guid-S1
  And ParsedSegment(SentenceId="p1-s1") 包含 3 个 Token
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

### REQ-PARSE-08：失败任务重试

**优先级**: P1 | **状态**: 已实现

管理员可通过管理面板对 `failed` 状态的文档重新发起解析，无需重新上传文件。

- 端点：`POST /admin/documents/{id}/retry`
- 前置条件：文档状态必须为 `failed`，否则返回 422
- 重试逻辑：
  1. 清除文档关联的旧数据（pages、segments、questions、occurrences）
  2. 重置文档状态为 `pending`
  3. 创建新的 ingestion job（`pending`）
  4. IngestionWorker 下一轮轮询自动处理
- 不需要重新上传文件，OSS 中的文件继续使用

**验收场景**:
```gherkin
Scenario: 重试失败文档
  Given 文档 status = "failed"
  When 调用 POST /admin/documents/{id}/retry
  Then 文档 status = "pending"
  And 新建一个 pending 状态的 ingestion job
  And IngestionWorker 自动处理该任务

Scenario: 非 failed 状态不可重试
  Given 文档 status = "ready"
  When 调用 POST /admin/documents/{id}/retry
  Then 返回 422，错误码 DOCRETRIEVAL_DOCUMENT_NOT_FAILED
```

---

### REQ-PARSE-10：LLM 智能文档分段

**优先级**: P0 | **状态**: 待实现

使用 LLM 替代规则切割，实现基于文档类型和学科的智能分段。

- 两阶段处理：
  1. 阶段一：文档分析（1 次 LLM 调用）→ 识别学科、年级、年份、文档类型、分段策略
  2. 阶段二：智能分段（按容量切块，每块 1 次 LLM 调用）→ 基于文档画像分段
- 支持的学科：English、语文、数学、物理、化学、生物、其他
- 支持的文档类型：教材、知识点过关单、单词表、短语表、试卷、其他
- 分段策略：sentence（按句子）、concept（按概念）、word_entry（按词条）、question（按题目）、knowledge_point（按知识点）
- 切块策略：按 ChunkSize 容量切块，优先在段落边界断开（详见 REQ-PARSE-13）
- LLM 调用失败时回退到现有规则切割
- 初始拆分与 refine 共享同一套切块 + LLM 调用逻辑

**验收场景**:
```gherkin
Scenario: English 教材按句子分段
  Given 文档学科 = "English"，文档类型 = "教材"
  When LLM 分析完成
  Then 分段策略 = "sentence"
  And 每个 segment 是一个完整的英语句子

Scenario: 数学教材按概念分段
  Given 文档学科 = "数学"，文档类型 = "教材"
  When LLM 分析完成
  Then 分段策略 = "concept"
  And 每个 segment 包含一个完整的数学概念或公式

Scenario: 单词表按词条分段
  Given 文档学科 = "English"，文档类型 = "单词表"
  When LLM 分析完成
  Then 分段策略 = "word_entry"
  And 每个 segment 包含一个完整的词条（单词+释义+例句）

Scenario: 试卷按题目分段
  Given 文档学科 = "数学"，文档类型 = "试卷"
  When LLM 分析完成
  Then 分段策略 = "question"
  And 每个 segment 包含一道完整的题目（题干+选项）

Scenario: LLM 调用失败时按策略决定回退行为
  Given LLM 服务调用失败或返回格式异常
  When 分段流程执行
  And 文档画像策略 = "sentence"
  Then 回退到现有 SplitSentences 规则切割
  And 记录 LogWarning 日志

Scenario: 单词表 LLM 失败触发任务级失败
  Given LLM 服务调用失败
  And 文档画像策略 = "word_entry"
  When 分段流程执行
  Then 记录 LogError 日志
  And 抛出 InvalidOperationException 触发任务级失败（job.Status = "failed"）
  And 数据库不写入任何 document_segments 记录（避免整段单条记录污染搜索结果）
  And 管理员可通过 POST /admin/documents/{id}/retry 重试

Scenario: 其它非 sentence 策略 LLM 失败触发任务级失败
  Given LLM 服务调用失败
  And 文档画像策略 ∈ {concept, question, knowledge_point}
  When 分段流程执行
  Then 同上 scenario "单词表 LLM 失败触发任务级失败"
```

---

### REQ-PARSE-11：LLM 分段配置

**优先级**: P1 | **状态**: 已实现

LLM 分段服务支持配置化，可通过配置文件或环境变量调整。

- 配置项：
  - `LlmSegmentation.ApiKey`：API 密钥（为空时禁用 LLM 分段）
  - `LlmSegmentation.BaseUrl`：API 基础 URL
  - `LlmSegmentation.Model`：模型 ID
  - `LlmSegmentation.ContextLength`：上下文窗口，支持 "128K"、"1M" 格式
  - `LlmSegmentation.MaxTokens`：最大输出 token，支持 "4K"、"128K" 格式
  - `LlmSegmentation.TimeoutSeconds`：HTTP 超时秒数（默认 300）
- 支持通过环境变量覆盖配置

**验收场景**:
```gherkin
Scenario: 通过配置文件设置 LLM 参数
  Given appsettings.json 中配置了 LlmSegmentation.Provider = "openai"
  When 服务启动
  Then LlmSegmentationService 使用 openai 提供商

Scenario: 通过环境变量覆盖配置
  Given 环境变量 LLM_SEGMENTATION__PROVIDER = "anthropic"
  When 服务启动
  Then LlmSegmentationService 使用 anthropic 提供商

Scenario: 配置缺失时使用默认值
  Given LlmSegmentation.Provider 未配置
  When 服务启动
  Then LlmSegmentationService 使用默认值（openai）
```

---

### REQ-PARSE-08b：手动取消任务

**优先级**: P1 | **状态**: 已实现

管理员可通过管理面板取消正在排队或处理中的导入任务。

- 端点：`POST /admin/documents/{id}/cancel`
- 前置条件：文档当前 job 状态为 `pending` 或 `processing`，否则返回 422
- 取消逻辑：
  1. 标记 job status = `cancelled`
  2. 标记 document status = `cancelled`
  3. 不清除已写入的解析数据（保留部分结果供参考）
- 注意：如果任务正在 IngestionWorker 中执行（已过 ParseAsync 阶段），取消不会中断正在进行的数据库写入，仅在下一轮状态检查时生效

**验收场景**:
```gherkin
Scenario: 取消 pending 任务
  Given 文档有一个 pending 状态的 job
  When 调用 POST /admin/documents/{id}/cancel
  Then job.Status = "cancelled" 且 document.Status = "cancelled"

Scenario: 取消 processing 任务
  Given 文档有一个 processing 状态的 job
  When 调用 POST /admin/documents/{id}/cancel
  Then job.Status = "cancelled" 且 document.Status = "cancelled"

Scenario: 无可取消任务
  Given 文档 job 状态为 "success"
  When 调用 POST /admin/documents/{id}/cancel
  Then 返回 422，错误码 DOCRETRIEVAL_JOB_NOT_CANCELLABLE
```

---

### REQ-PARSE-09：错误处理与故障恢复

**优先级**: P0 | **状态**: 已实现

解析过程中任何异常均需妥善处理，确保系统稳定性。

- **任务级异常**：每个任务在独立 try/catch 中处理，捕获 Exception 后调用 `FailIngestionJobAsync(job.Id, ex.Message)`
- **FailIngestionJobAsync 自身异常**：嵌套 try/catch，记录日志 `"标记导入任务失败时出错：{JobId}"`
- **轮询级异常**：外层 try/catch 捕获轮询过程中的异常，记录日志 `"导入工作器轮询出错"`，Worker 继续运行
- **索引写入异常**：搜索索引写入失败仅记日志，不标记任务失败

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

### REQ-PARSE-12：Word 文档分页检测

**优先级**: P0 | **状态**: 待实现

Word 文档（.docx）的文件格式中不包含固定物理页面信息，页码是渲染时根据纸张大小、字体、边距动态计算的。当前实现将整个 Word 文档作为单页处理（`PageNumber = 1`），导致所有 segment 的页码均为 1，丢失了页码定位信息。

**检测策略**：仅检测 XML 中的显式分页标记，不进行估算。检测优先级从高到低：

1. **显式分页符**（`w:br type="page"`）：用户通过 Ctrl+Enter 插入的分页符，对应 OpenXml `Break` 类型 `BreakValues.Page`
2. **段前分页**（`w:pageBreakBefore`）：段落属性中的"段前分页"设置，对应 OpenXml `PageBreakBefore` 类
3. **样式级段前分页**：段落引用的样式中包含 `PageBreakBefore` 属性
4. **节分隔符**（`w:sectPr`）：`SectionType` 为 `NextPage`/`EvenPage`/`OddPage` 的节分隔符（`Continuous` 不产生分页）

**降级行为**：若以上 4 种信号均未检测到，文档保持为单页（`PageNumber = 1`），并记录 `LogWarning` 日志提示"未检测到分页标记"。

**不处理的场景**：
- 软分页（内容溢出自动换页）：XML 中不存在此信息，OpenXml 无法检测
- `LastRenderedPageBreak`：仅 Word 保存时写入的布局缓存，不可靠，不作为检测信号

**验收场景**:
```gherkin
Scenario: 含显式分页符的 Word 文档正确分页
  Given Word 文档包含 2 个显式分页符（Ctrl+Enter）
  When 调用 ParseAsync
  Then 返回 ParsedDocument 包含 3 个 ParsedPage（PageNumber = 1, 2, 3）

Scenario: 含节分隔符的 Word 文档正确分页
  Given Word 文档包含 1 个 NextPage 类型的节分隔符
  When 调用 ParseAsync
  Then 返回 ParsedDocument 包含 2 个 ParsedPage

Scenario: 无分页标记的 Word 文档降级为单页
  Given Word 文档无任何显式分页标记
  When 调用 ParseAsync
  Then 返回 ParsedDocument 包含 1 个 ParsedPage（PageNumber = 1）
  And 日志输出 LogWarning "未检测到分页标记"

Scenario: Continuous 节分隔符不产生分页
  Given Word 文档包含 1 个 Continuous 类型的节分隔符
  When 调用 ParseAsync
  Then 该节分隔符不被识别为分页标记
```

---

### REQ-PARSE-13：按容量切块 LLM 分段

**优先级**: P0 | **状态**: 待实现

LLM 分段不再按物理边界（block/页）逐个调用，而是按 ChunkSize 容量切块，充分利用上下文窗口，减少 LLM 调用次数。

**切块策略**：
1. 提取所有页面文本，拼接为连续文本流，页面间用 `\n\n` 分隔
2. 维护 `offset → pageNumber` 映射表
3. 按 ChunkSize 切块，优先在段落边界（`\n\n`）处断开，不在句子中间切
4. 每个块发 1 次 LLM 调用
5. LLM 返回的 segment 按 offset 回映射到原始页码

**初始拆分与 refine 共享**：
- 两种流程使用相同的 `ChunkByCapacity` 切块逻辑
- 两种流程使用相同的 `MapOffsetToPage` 回映射逻辑
- 区别仅在于：初始拆分调用 `SegmentTextAsync`，refine 调用 `RefineSegmentTextAsync`

**SentenceId 格式**：`p{pageNumber}-s{segmentIndex}`（移除 block 层级）

**验收场景**:
```gherkin
Scenario: 20 页文档按容量切块
  Given 文档有 20 页，总文本约 30000 字符
  And ChunkSize = 5000 字符（上限）
  When 调用 LLM 分段
  Then LLM 调用次数为 6（30000 / 5000），并行执行
  And 返回的 segment 按 offset 回映射到正确的 PageNumber

Scenario: 超长文档分多个 chunk
  Given 文档有 100 页，总文本约 200000 字符
  And ChunkSize = 5000 字符
  When 调用 LLM 分段
  Then LLM 调用次数为 40，并行执行
  And 每个 chunk 的 segment 都映射到正确的 PageNumber

Scenario: 跨页逻辑块保持完整
  Given 一道题目从第 3 页末尾开始，选项在第 4 页开头
  And 两页文本在同一个 chunk 内
  When LLM 分段
  Then 该题目被识别为 1 个完整 segment
  And segment 的 PageNumber = 3（以题干所在页为准）

Scenario: refine 与初始拆分共享切块逻辑
  Given 文档已入库，用户提交修正
  When 调用 refine 流程
  Then 使用与初始拆分相同的 ChunkByCapacity 切块
  And LLM 调用次数与初始拆分相同
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
| 可扩展性 | 搜索索引为可选依赖 | nullable 注入 |
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
