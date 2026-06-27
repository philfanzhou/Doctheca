# 03-DESIGN — DocumentParsing 设计文档

## 文件结构

```
src/
├── Service/
│   ├── IngestionWorker.cs              # 后台工作器，轮询 + 编排
│   ├── DocumentParserService.cs        # 解析器实现（PDF/Word/PPT）
│   ├── LlmSegmentationService.cs      # LLM 智能分段服务
│   ├── OpenSearchIndexService.cs       # OpenSearch 搜索索引服务
├── Domain/
│   ├── Models/
│   │   ├── DocumentModel.cs              # 文档领域模型
│   │   ├── DocumentPageModel.cs          # 文档页面领域模型
│   │   ├── DocumentSegmentModel.cs       # 文档片段领域模型
│   │   ├── QuestionSegmentModel.cs       # 题目片段领域模型
│   │   ├── DocumentOccurrenceModel.cs    # 文档出现记录领域模型
│   │   ├── DocumentIngestionJobModel.cs  # 导入任务领域模型
│   │   ├── ParsedDocument.cs             # 解析器输出模型
│   │   ├── ParsedPage.cs                 # 解析器输出页面
│   │   ├── ParsedSegment.cs              # 解析器输出片段
│   │   ├── ParsedQuestion.cs             # 解析器输出题目
│   │   ├── ParsedToken.cs                # 解析器输出词元
│   │   ├── DocLibraryConstants.cs      # 常量定义（学科、年级）
│   │   ├── DocumentStatus.cs             # 文档状态常量
│   │   ├── SegmentTypes.cs               # 片段类型常量
│   │   ├── SourceTypes.cs                # 来源类型常量
│   │   ├── SearchMatchType.cs            # 搜索匹配类型常量
│   │   ├── SearchConfig.cs              # 搜索配置模型
│   │   ├── DocumentProfile.cs           # LLM 文档画像模型
│   │   └── SegmentResult.cs             # LLM 分段结果模型
│   ├── Repositories/
│   │   ├── IDocumentParserService.cs     # 解析器接口
│   │   ├── ILlmSegmentationService.cs   # LLM 分段服务接口
│   │   ├── ISearchIndexService.cs        # 搜索索引接口
│   │   ├── IDocumentRepository.cs        # 文档仓储接口
│   │   ├── IDocumentPageRepository.cs    # 文档页面仓储接口
│   │   ├── IDocumentSegmentRepository.cs # 文档片段仓储接口
│   │   ├── IQuestionSegmentRepository.cs # 题目片段仓储接口
│   │   ├── IDocumentOccurrenceRepository.cs # 文档出现记录仓储接口
│   │   ├── IDocumentIngestionJobRepository.cs # 导入任务仓储接口
│   │   └── IUnitOfWork.cs               # 事务接口
│   └── Services/
│       └── DocumentDomainService.cs      # 领域服务（任务状态管理）
├── Database/
│   ├── Entities/
│   │   ├── DocumentEntity.cs
│   │   ├── DocumentIngestionJobEntity.cs
│   │   ├── DocumentPageEntity.cs
│   │   ├── DocumentSegmentEntity.cs
│   │   ├── DocumentOccurrenceEntity.cs
│   │   └── QuestionSegmentEntity.cs
│   ├── Repositories/
│   │   ├── DocumentIngestionJobRepository.cs
│   │   ├── DocumentPageRepository.cs
│   │   ├── DocumentSegmentRepository.cs
│   │   ├── DocumentOccurrenceRepository.cs
│   │   ├── DocumentRepository.cs
│   │   ├── QuestionSegmentRepository.cs
│   │   └── UnitOfWork.cs
│   └── DocLibraryDbContext.cs
└── Host/
    └── Program.cs                      # DI 注册
```

## 接口签名

### IngestionWorker

```csharp
public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly ISearchIndexService? _searchIndexService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public IngestionWorker(
        IServiceProvider serviceProvider,
        ILogger<IngestionWorker> logger,
        ISearchIndexService? searchIndexService = null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken);
}
```

### DocumentDomainService — 任务管理方法

```csharp
public class DocumentDomainService
{
    // 标记任务 processing，同步更新 document.Status = "processing"
    public async Task StartIngestionJobAsync(Guid jobId, string parserVersion, string? ocrVersion);

    // 标记任务 success，同步更新 document.Status = "ready"
    public async Task CompleteIngestionJobAsync(Guid jobId);

    // 标记任务 failed，记录 errorMessage，同步更新 document.Status = "failed"
    public async Task FailIngestionJobAsync(Guid jobId, string errorMessage);

    // 获取所有 pending 状态的任务
    public async Task<List<DocumentIngestionJobModel>> GetPendingJobsAsync();

    // 获取文档信息
    public async Task<DocumentModel?> GetDocumentAsync(Guid id);

    // 重试失败文档：清除旧数据 → 重置状态 → 创建新 job
    public async Task RetryIngestionAsync(Guid documentId);
}
```

### 重试端点

```
POST /admin/documents/{id}/retry
```

**前置条件**：文档 status = `failed`，否则返回 422。

**流程**：
1. 清除旧数据（pages、segments、questions、occurrences）
2. 重置 document.Status = `pending`
3. 创建新 ingestion job（status = `pending`）
4. IngestionWorker 下一轮自动处理

### 取消端点

```
POST /admin/documents/{id}/cancel
```

**前置条件**：当前 job 状态为 `pending` 或 `processing`，否则返回 422。

**流程**：
1. 标记 job.Status = `cancelled`，job.FinishedAt = now
2. 标记 document.Status = `cancelled`，document.UpdatedAt = now
3. 不清除已写入的解析数据

### IDocumentParserService

```csharp
public interface IDocumentParserService
{
    /// <param name="fileStream">文件流</param>
    /// <param name="sourceType">文件类型，使用 SourceTypes 常量（pdf/word/ppt）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default);
}
```

> **注意**：`sourceType` 使用 `SourceTypes` 常量（`pdf`/`word`/`ppt`），不是文件扩展名（`docx`/`pptx`）。`SourceTypes.Unknown` 不可解析，调用方应在校验阶段拦截。

### ISearchIndexService

```csharp
public interface ISearchIndexService
{
    Task EnsureIndexAsync();
    Task IndexDocumentSegmentsAsync(Guid documentId, string documentTitle, string subject, string grade, string year);
    Task DeleteDocumentIndexAsync(Guid documentId);
    Task UpdateDocumentMetadataAsync(Guid documentId, string subject, string grade, string year);
    Task<(List<SearchResultModel> Results, int TotalCount, string? NextToken)> ExactSearchAsync(...);
}
```

## Word 文档分页检测设计

### 背景

Word .docx 格式与 PDF/PPT 不同：文件中只存储段落流，不包含物理页面信息。页码是渲染时根据纸张大小、字体、边距动态计算的。OpenXml SDK 无排版引擎，无法计算软分页。

### 检测策略（REQ-PARSE-12）

仅检测 XML 中的显式分页标记，不估算。检测按以下优先级遍历每个段落：

```
对每个 Paragraph 依次检查：
  1. 段落内是否有 w:br type="page"（Break with BreakValues.Page）
  2. 段落属性是否有 w:pageBreakBefore（PageBreakBefore，Val 为 null 或 true）
  3. 段落引用的样式是否包含 PageBreakBefore
  4. 段落属性内是否有 w:sectPr 且 SectionType != Continuous
```

检测到分页标记的段落成为新页的起始段落。未检测到任何分页标记时，整个文档作为单页处理。

### 辅助方法

```csharp
/// <summary>
/// 检测 Word 文档中的分页标记，返回每个分页标记对应的段落索引。
/// 检测信号（按优先级）：
///   1. 显式分页符 (w:br type="page")
///   2. 段前分页 (w:pageBreakBefore)
///   3. 样式级段前分页
///   4. 节分隔符 (w:sectPr, 非 Continuous)
/// </summary>
private HashSet<int> DetectPageBreaks(WordprocessingDocument doc, List<Paragraph> paragraphs)
```

返回值：产生分页的段落索引集合。调用方根据此集合将段落流切分为多个 ParsedPage。

### 不处理的场景

| 场景 | 原因 |
|------|------|
| 软分页（内容溢出） | XML 中不存在，需排版引擎 |
| `LastRenderedPageBreak` | Word 布局缓存，非 Word 编辑器不写入，可能过期 |
| `Continuous` 节分隔符 | 不产生页面边界，仅改变版式 |

## 按容量切块 LLM 分段设计（REQ-PARSE-13）

### 背景

原有设计按段落逐个调用 LLM，存在调用次数多、上下文不足、跨页逻辑块被切断等问题。新设计改为按 ChunkSize 容量切块，充分利用 LLM 上下文窗口。

### 核心流程

```
提取页面文本 → pages: List<(PageNumber, Text)>
  → ChunkByCapacity(pages, chunkSize) → chunks: List<TextChunk>
  → ProcessChunksAsync: Task.WhenAll（所有 chunk 并行调用 LLM）
    → foreach chunk result:
        MapOffsetToPage(chunk.PageRanges, segment.StartOffset) → pageNumber
        构建 ParsedSegment { PageNumber, SentenceId = $"p{N}-s{K}" }
```

### TextChunk 结构

```csharp
private record TextChunk
{
    public string Text { get; init; }
    public int GlobalStartOffset { get; init; }
    public List<PageRange> PageRanges { get; init; }
}

private record PageRange
{
    public int ChunkStartOffset { get; init; }  // chunk 内起始 offset
    public int ChunkEndOffset { get; init; }    // chunk 内结束 offset
    public int PageNumber { get; init; }        // 对应页码
}
```

### ChunkByCapacity 算法

1. 拼接所有页面文本，页面间用 `\n\n` 分隔
2. 维护 `globalOffset → pageNumber` 的映射区间
3. 从 `globalOffset = 0` 开始，累积文本直到接近 `chunkSize`
4. 在最近的 `\n\n`（段落边界）处断开
5. 若段落边界距离 `chunkSize` 过远（> 20%），在空格处断开
6. 每个 chunk 记录 `GlobalStartOffset` 和 `PageRanges`

### MapOffsetToPage 算法

给定 chunk 的 `PageRanges` 和 segment 的 `StartOffset`（chunk 内偏移），二分查找对应的 `PageNumber`。若 segment 跨页，以起始位置所在页为准。

### 与 Refine 共享

`ChunkByCapacity` 和 `MapOffsetToPage` 为 `internal static` 方法，`DocumentDomainService.RefineSegmentsAsync` 也调用同一套逻辑。

Refine 流程：
1. 从 DB 读取现有 segments，按 PageId 分组
2. 用 segment text 拼回页面文本，构建 `List<(PageNumber, Text)>`
3. 调用 `ChunkByCapacity` 切块
4. 每个 chunk 调 `RefineSegmentTextAsync`（带 corrections）
5. `MapOffsetToPage` 回映射到页码

## 数据模型

### ParsedDocument 结构（解析器输出）

```
ParsedDocument
└── Pages: List<ParsedPage>
    ├── PageNumber: int
    ├── Segments: List<ParsedSegment>
    │   ├── SentenceId: string       // "p{pageNumber}-s{segmentIndex}"
    │   ├── SegmentType: string      // "sentence"
    │   ├── Text: string
    │   ├── StartOffset: int
    │   ├── EndOffset: int
    │   └── Tokens: List<ParsedToken>
    │       ├── TokenText: string
    │       ├── TokenStem: string    // Porter 词干还原
    │       ├── StartOffset: int
    │       └── EndOffset: int
    └── Questions: List<ParsedQuestion>
        ├── QuestionId: string       // "p{pageNumber}-q{number}"
        ├── Stem: string
        ├── OptionsJson: string?     // JSON 序列化的选项字典
        ├── AnswerArea: string?
        ├── StartOffset: int
        ├── EndOffset: int
        └── Tokens: List<ParsedToken>
```

### 数据库模型映射

```
ParsedPage         → DocumentPageModel
                       Id = Guid.NewGuid()
                       DocumentId = document.Id
                       PageNumber = p.PageNumber

ParsedSegment      → DocumentSegmentModel
                       Id = Guid.NewGuid()
                       DocumentId = document.Id
                       PageId = pageLookup[page.PageNumber]
                       SentenceId / SegmentType / Text / StartOffset / EndOffset

ParsedQuestion     → QuestionSegmentModel
                       Id = Guid.NewGuid()
                       DocumentId = document.Id
                       PageId = pageLookup[page.PageNumber]
                       QuestionId / Stem / OptionsJson / AnswerArea / StartOffset / EndOffset

ParsedToken (seg)  → DocumentOccurrenceModel
                       SegmentId = segmentLookup[seg.SentenceId]
                       QuestionSegmentId = null

ParsedToken (q)    → DocumentOccurrenceModel
                       SegmentId = null
                       QuestionSegmentId = questionLookup[question.QuestionId]
```

### 映射字典构建

| 映射 | 构建方式 | 用途 |
|------|---------|------|
| pageLookup | `pageModels.ToDictionary(p => p.PageNumber, p => p.Id)` | Segment/Question 的 PageId 赋值 |
| segmentLookup | `segmentModels.ToDictionary(s => s.SentenceId, s => s.Id)` | Occurrence 的 SegmentId 赋值 |
| questionLookup | `questionModels.Where(qm => page.Questions.Any(pq => pq.QuestionId == qm.QuestionId)).ToDictionary(qm => qm.QuestionId, qm => qm.Id)` | Occurrence 的 QuestionSegmentId 赋值 |

## 解析流程图

```
┌─────────────────────────────────────────────────────────────────┐
│                    IngestionWorker.ExecuteAsync                  │
│                                                                 │
│  ┌─────────────────── while (!stoppingToken.IsCancellationRequested) ───┐
│  │                                                                      │
│  │  using scope = _serviceProvider.CreateScope()                        │
│  │  ├── domainService   = scope.ServiceProvider.GetRequiredService<...>()│
│  │  ├── parserService   = scope.ServiceProvider.GetRequiredService<...>()│
│  │  ├── ossService      = scope.ServiceProvider.GetRequiredService<...>()│
│  │  ├── pageRepository  = scope.ServiceProvider.GetRequiredService<...>()│
│  │  ├── segmentRepository = scope.ServiceProvider.GetRequiredService<...>()│
│  │  ├── questionRepository = scope.ServiceProvider.GetRequiredService<...>()│
│  │  └── occurrenceRepository = scope.ServiceProvider.GetRequiredService<...>()│
│  │                                                                      │
│  │  pendingJobs = domainService.GetPendingJobsAsync()                   │
│  │                                                                      │
│  │  foreach (job in pendingJobs):                                       │
│  │  ┌── try ──────────────────────────────────────────────────────┐     │
│  │  │                                                              │     │
│  │  │  ① StartIngestionJobAsync(job.Id, "v1.0", null)             │     │
│  │  │     → job.Status = "processing", document.Status = "processing" │  │
│  │  │     → UpdateJobProgressAsync(5, "starting")                  │     │
│  │  │                                                              │     │
│  │  │  ② document = domainService.GetDocumentAsync(job.DocumentId) │     │
│  │  │                                                              │     │
│  │  │  ③ fileStream = ossService.DownloadAsync(document.FilePath) │     │
│  │  │     fileStream == null → 抛异常 "文件不存在于 OSS"          │     │
│  │  │     → UpdateJobProgressAsync(10, "downloading")              │     │
│  │  │                                                              │     │
│  │  │  ④ parsedDocument = parserService.ParseAsync(fileStream,    │     │
│  │  │         document.SourceType, progress, stoppingToken)        │     │
│  │  │     parsedDocument.Pages.Count == 0 → LogWarning "解析结果为空" │  │
│  │  │                                                              │     │
│  │  │     注：ParseAsync 内部流程（详见 07-LLM-SEGMENTATION.md）：  │     │
│  │  │     4b. LLM 文档分析 → DocumentProfile（学科+类型+策略）     │     │
│  │  │         → progress.Report({Stage:"analyzing", 1/1})          │     │
│  │  │         → UpdateJobProgressAsync(10~20, "analyzing")         │     │
│  │  │     4c. 按容量切块（ChunkByCapacity）                        │     │
│  │  │     4d. 每块 LLM 分段 → List<SegmentResult>                 │     │
│  │  │          ├─ LLM 成功：使用 LLM 返回的 segments                │     │
│  │  │          └─ LLM 失败/空响应：进入"策略感知回退"判定         │     │
│  │  │              ├─ profile.SegmentStrategy == "sentence"        │     │
│  │  │              │   → 回退到 SplitSentences（保留旧行为）        │     │
│  │  │              └─ 其它策略（word_entry/concept/question/       │     │
│  │  │                  knowledge_point）                            │     │
│  │  │                  → 抛 InvalidOperationException 触发 ④ 外层  │     │
│  │  │                    catch → 任务级失败（见 ② 路径）           │     │
│  │  │     4e. 防御性过滤：仅当 segments.Count > 1 且            │     │
│  │  │         text.Length >= 0.9 * chunkLength 时丢弃该        │     │
│  │  │         segment（LLM 偶发返回的整块摘要/标题；            │     │
│  │  │         孤立 1 个 segment 不过滤以兼容合法短文本）        │     │
│  │  │     4f. 回映射到页码 + 构建 ParsedSegment + Tokenize        │     │
│  │  │     每个 chunk 完成时：                                      │     │
│  │  │         progress.Report({Stage:"parsing", completed/total})  │     │
│  │  │         → UpdateJobProgressAsync(20~75, "parsing")           │     │
│  │  │                                                              │     │
│  │  │  ⑤ 写入 pages                                               │     │
│  │  │     pageModels = parsedDocument.Pages → DocumentPageModel[]  │     │
│  │  │     pageRepository.AddRangeAsync(pageModels)                 │     │
│  │  │     pageLookup = pageModels.ToDictionary(...)               │     │
│  │  │     → UpdateJobProgressAsync(80, "writing_pages")            │     │
│  │  │                                                              │     │
│  │  │  ⑥ 写入 segments                                            │     │
│  │  │     segmentModels = parsedDocument.Pages.SelectMany(...)     │     │
│  │  │     segmentRepository.AddRangeAsync(segmentModels)           │     │
│  │  │     segmentLookup = segmentModels.ToDictionary(...)         │     │
│  │  │                                                              │     │
│  │  │  ⑦ 写入 questions                                           │     │
│  │  │     questionModels = parsedDocument.Pages.SelectMany(...)    │     │
│  │  │     questionRepository.AddRangeAsync(questionModels)         │     │
│  │  │                                                              │     │
│  │  │  ⑧ 写入 occurrences (Token)                                 │     │
│  │  │     遍历 segment.Tokens → SegmentId 映射                    │     │
│  │  │     遍历 question.Tokens → QuestionSegmentId 映射           │     │
│  │  │     occurrenceRepository.AddRangeAsync(occurrenceModels)     │     │
│  │  │     → UpdateJobProgressAsync(90, "writing_segments")        │     │
│  │  │                                                              │     │
│  │  │  ⑨ CompleteIngestionJobAsync(job.Id)                        │     │
│  │  │     → job.Status = "success", document.Status = "ready"     │     │
│  │  │                                                              │     │
│  │  │  ⑩ 同步搜索索引（可选，失败仅记日志）                       │     │
│  │  │     _searchIndexService?.IndexDocumentSegmentsAsync(...)     │     │
│  │  │     → UpdateJobProgressAsync(100, "indexing")                │     │
│  │  │                                                              │     │
│  │  │  ⑪ 最终完成                                                 │     │
│  │  │     → UpdateJobProgressAsync(100, "completed")              │     │
│  │  │                                                              │     │
│  │  ├── catch (Exception ex) ─────────────────────────────────────┐    │
│  │  │  domainService.FailIngestionJobAsync(job.Id, ex.Message)     │    │
│  │  │  → job.Status = "failed", document.Status = "failed"        │    │
│  │  │  → job.ErrorMessage = ex.Message                            │    │
│  │  │  (FailIngestionJobAsync 自身失败也 try/catch 保护)          │    │
│  │  └──────────────────────────────────────────────────────────────┘    │
│  │                                                                      │
│  │  catch (Exception ex) → 记录 "导入工作器轮询出错" 日志              │
│  │                                                                      │
│  │  await Task.Delay(_pollInterval, stoppingToken)                      │
│  └──────────────────────────────────────────────────────────────────────┘
└─────────────────────────────────────────────────────────────────┘
```

## 错误处理策略

### 异常层级

```
ExecuteAsync
├── 外层 try/catch          → 轮询级异常，Worker 继续运行
│   ├── scope 创建
│   ├── GetPendingJobsAsync
│   └── foreach (job)
│       └── 任务级 try/catch → 单任务异常，标记 failed，继续下一个
│           ├── StartIngestionJobAsync
│           ├── 下载/解析/写入
│           ├── CompleteIngestionJobAsync
│           ├── 搜索索引 try/catch  → 失败仅记日志
│       └── catch → FailIngestionJobAsync (嵌套 try/catch)
└── Task.Delay
```

### 错误分类

| 错误类型 | 处理方式 | 影响 |
|---------|---------|------|
| 文档不存在 | 抛出 InvalidOperationException | 任务标记 failed |
| OSS 下载失败 | 抛出异常 | 任务标记 failed |
| OSS 文件为空/null | 抛出 InvalidOperationException | 任务标记 failed |
| 解析结果零页 | LogWarning，任务仍标记 success | 文档 searchable 但无内容 |
| 不支持的文件类型 | 抛出 NotSupportedException | 任务标记 failed |
| 解析器内部错误 | 抛出异常 | 任务标记 failed |
| 数据库写入失败 | 抛出异常 | 任务标记 failed |
| 搜索索引写入失败 | try/catch + LogError | 不影响任务状态 |
| FailIngestionJobAsync 失败 | 清除 ChangeTracker + 重试 | 保证失败状态写入 |
| 重复 QuestionId | GroupBy 去重，保留首个 | 避免唯一约束冲突 |
| 轮询级异常 | 外层 try/catch + LogError | Worker 继续运行 |

## 外部依赖

| 依赖 | 用途 | 注入方式 | 可选 |
|------|------|---------|------|
| IOssService | 从 OSS 下载文件 | Scoped（通过 CreateScope） | 否 |
| IDocumentParserService | 文档解析 | Scoped（通过 CreateScope） | 否 |
| DocumentDomainService | 任务状态管理 | Scoped（通过 CreateScope） | 否 |
| IDocumentPageRepository | Page 写入 | Scoped（通过 CreateScope） | 否 |
| IDocumentSegmentRepository | Segment 写入 | Scoped（通过 CreateScope） | 否 |
| IQuestionSegmentRepository | Question 写入 | Scoped（通过 CreateScope） | 否 |
| IDocumentOccurrenceRepository | Occurrence 写入 | Scoped（通过 CreateScope） | 否 |
| ISearchIndexService | 搜索索引同步 | 构造函数注入（Singleton） | 是 |

### 解析器内部依赖

| 依赖 | NuGet 包 | 用途 |
|------|---------|------|
| PdfPig | UglyToad.PdfPig | PDF 文本提取 |
| OpenXml SDK | DocumentFormat.OpenXml | Word/PPT 文本提取 |
| LLM 分段服务 | - | 智能文档分段（替代规则切割） |

> **注意**：LLM 分段服务的设计详见 [07-LLM-SEGMENTATION.md](./07-LLM-SEGMENTATION.md)

### 索引服务外部依赖

| 依赖 | 用途 | 配置 |
|------|------|------|
| OpenSearch | BM25 全文搜索 | `OpenSearchOptions.Url`、`OpenSearchOptions.IndexName` |
