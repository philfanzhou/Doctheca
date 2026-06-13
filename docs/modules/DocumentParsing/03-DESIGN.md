# 03-DESIGN — DocumentParsing 设计文档

## 文件结构

```
src/
├── Service/
│   ├── IngestionWorker.cs              # 后台工作器，轮询 + 编排
│   ├── DocumentParserService.cs        # 解析器实现（PDF/Word/PPT）
│   ├── OpenSearchIndexService.cs       # OpenSearch 搜索索引服务
├── Domain/
│   ├── Models/
│   │   ├── DocumentModels.cs           # 所有领域模型（含 ParsedDocument）
│   │   ├── Constants.cs               # 常量定义（学科、年级）
│   │   └── SearchConfig.cs            # 搜索配置模型
│   ├── Repositories/
│   │   ├── IDocumentParserService.cs   # 解析器接口
│   │   ├── ISearchIndexService.cs      # 搜索索引接口
│   │   └── IRepositories.cs           # 仓储接口集合
│   └── Services/
│       └── DocumentDomainService.cs    # 领域服务（任务状态管理）
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
│   └── DocRetrievalDbContext.cs
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
}
```

### IDocumentParserService

```csharp
public interface IDocumentParserService
{
    /// <param name="fileStream">文件流</param>
    /// <param name="sourceType">文件类型（pdf/docx/pptx）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<ParsedDocument> ParseAsync(Stream fileStream, string sourceType, CancellationToken cancellationToken = default);
}
```

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

## 数据模型

### ParsedDocument 结构（解析器输出）

```
ParsedDocument
└── Pages: List<ParsedPage>
    ├── PageNumber: int
    ├── Segments: List<ParsedSegment>
    │   ├── BlockId: string          // "p{pageNumber}-b{blockIndex}"
    │   ├── SentenceId: string       // "{blockId}-s{sentIndex}"
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
        ├── QuestionId: string       // "q{number}"
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
                       BlockId / SentenceId / SegmentType / Text / StartOffset / EndOffset

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
│  │  │                                                              │     │
│  │  │  ② document = domainService.GetDocumentAsync(job.DocumentId) │     │
│  │  │                                                              │     │
│  │  │  ③ fileStream = ossService.DownloadAsync(document.FilePath) │     │
│  │  │                                                              │     │
│  │  │  ④ parsedDocument = parserService.ParseAsync(fileStream,    │     │
│  │  │         document.SourceType, stoppingToken)                  │     │
│  │  │                                                              │     │
│  │  │  ⑤ 写入 pages                                               │     │
│  │  │     pageModels = parsedDocument.Pages → DocumentPageModel[]  │     │
│  │  │     pageRepository.AddRangeAsync(pageModels)                 │     │
│  │  │     pageLookup = pageModels.ToDictionary(...)               │     │
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
│  │  │                                                              │     │
│  │  │  ⑨ CompleteIngestionJobAsync(job.Id)                        │     │
│  │  │     → job.Status = "success", document.Status = "ready"     │     │
│  │  │                                                              │     │
│  │  │  ⑩ 同步搜索索引（可选，失败仅记日志）                       │     │
│  │  │     _searchIndexService?.IndexDocumentSegmentsAsync(...)     │     │
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
| 不支持的文件类型 | 抛出 NotSupportedException | 任务标记 failed |
| 解析器内部错误 | 抛出异常 | 任务标记 failed |
| 数据库写入失败 | 抛出异常 | 任务标记 failed |
| 搜索索引写入失败 | try/catch + LogError | 不影响任务状态 |
| FailIngestionJobAsync 失败 | 嵌套 try/catch + LogError | 仅记日志 |
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

### 索引服务外部依赖

| 依赖 | 用途 | 配置 |
|------|------|------|
| OpenSearch | BM25 全文搜索 | `OpenSearchOptions.Url`、`OpenSearchOptions.IndexName` |
