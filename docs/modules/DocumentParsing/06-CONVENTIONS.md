# 06-CONVENTIONS — DocumentParsing 代码约定

## 命名约定

### 类命名

| 类型 | 规则 | 示例 |
|------|------|------|
| 后台工作器 | `{Feature}Worker` | `IngestionWorker` |
| 领域服务 | `{Feature}DomainService` | `DocumentDomainService` |
| 解析器接口 | `I{Feature}ParserService` | `IDocumentParserService` |
| 解析器实现 | `{Feature}ParserService` | `DocumentParserService` |
| LLM 分段服务接口 | `I{Feature}Service` | `ILlmSegmentationService` |
| LLM 分段服务实现 | `{Feature}Service` | `LlmSegmentationService` |
| 索引服务接口 | `I{Technology}Service` | `ISearchIndexService` |
| 领域模型 | `{Entity}Model` | `DocumentModel`、`DocumentPageModel` |
| 解析结果模型 | `Parsed{Entity}` | `ParsedDocument`、`ParsedPage`、`ParsedSegment` |
| LLM 分段结果模型 | `{Entity}Result` | `SegmentResult` |
| LLM 文档画像模型 | `{Entity}Profile` | `DocumentProfile` |
| 数据库实体 | `{Entity}Entity` | `DocumentEntity`、`DocumentIngestionJobEntity` |
| 仓储接口 | `I{Entity}Repository` | `IDocumentPageRepository` |
| 配置选项 | `{Technology}Options` | `OpenSearchOptions`、`LlmSegmentationOptions` |
| 自定义异常 | `{Feature}ValidationException` | `DocRetrievalValidationException` |

### 方法命名

| 操作 | 前缀 | 示例 |
|------|------|------|
| 异步方法 | `Async` 后缀 | `ParseAsync`、`GetPendingJobsAsync` |
| 获取单个 | `Get` + 实体名 | `GetDocumentAsync`、`GetByIdAsync` |
| 获取列表 | `Get` + 实体名 + `List/ByStatus` | `GetListAsync`、`GetByStatusAsync` |
| 批量添加 | `AddRange` | `AddRangeAsync` |
| 删除 | `Delete` + 条件 | `DeleteByDocumentIdAsync` |
| 状态变更 | 动词 + 实体名 | `StartIngestionJobAsync`、`CompleteIngestionJobAsync`、`FailIngestionJobAsync` |
| 索引操作 | `Index/Delete/Ensure` + 目标 | `IndexDocumentSegmentsAsync`、`EnsureIndexAsync` |

### 字段命名

| 类型 | 规则 | 示例 |
|------|------|------|
| 私有字段 | `_` 前缀 + camelCase | `_serviceProvider`、`_pollInterval`、`_logger` |
| 模型属性 | PascalCase | `PageNumber`、`SentenceId`、`TokenStem` |
| 数据库列 | snake_case | `document_id`、`page_number`、`start_offset` |
| JSON 属性 | snake_case | `document_title`、`segment_type` |

### 标识符命名

| 标识符类型 | 格式 | 示例 |
|-----------|------|------|
| SentenceId | `p{pageNumber}-s{sentIndex}` | `p1-s1`、`p1-s2` |
| QuestionId | `p{pageNumber}-q{number}` | `p1-q1`、`p1-q12` |

## 日志约定

### 日志级别

| 级别 | 使用场景 | 示例 |
|------|---------|------|
| LogInformation | 正常流程关键节点 | Worker 启动、任务开始、解析完成、索引创建成功 |
| LogWarning | 非致命异常、可恢复问题 | PDF 页文本为空、索引创建失败 |
| LogError | 致命异常、需要关注 | 任务处理失败、FailIngestionJobAsync 失败、轮询出错 |

### 日志消息格式

```
{操作描述}：{关键标识符}
```

- 使用中文描述操作
- 使用 `{PropertyName}` 占位符传递参数（不使用字符串插值）
- 冒号后跟关键标识符，便于搜索和过滤

### 日志消息清单

| 场景 | 级别 | 消息模板 |
|------|------|---------|
| Worker 启动 | Information | `"文档导入后台工作器已启动"` |
| 任务开始处理 | Information | `"开始处理导入任务：{JobId}，文档：{DocumentId}"` |
| 解析完成 | Information | `"文档解析完成：{DocumentId}，共 {PageCount} 页"` |
| Token 写入完成 | Information | `"Token 写入完成：{DocumentId}，共 {TokenCount} 个 Token"` |
| 导入任务完成 | Information | `"导入任务完成：{JobId}，共 {SegmentCount} 个片段，{QuestionCount} 道题目"` |
| 搜索索引创建成功 | Information | `"文档搜索索引已创建：{DocumentId}"` |
| LLM 文档分析完成 | Information | `"文档分析完成：DocumentId={DocumentId}, Subject={Subject}, DocType={DocType}, Strategy={Strategy}"` |
| LLM 分段完成 | Information | `"LLM 分段完成：DocumentId={DocumentId}, ChunkCount={ChunkCount}, SegmentCount={SegmentCount}"` |
| PDF 页文本为空 | Warning | `"PDF 第 {PageNumber} 页文本为空，可能需要 OCR 支持"` |
| PPT 页文本为空 | Warning | `"PPT 第 {PageNumber} 页文本为空"` |
| LLM 分段失败回退 | Warning | `"LLM 分段失败，回退到规则切割：DocumentId={DocumentId}, Error={Error}"` |
| 搜索索引创建失败 | Error | `"创建文档搜索索引失败：{DocumentId}"` |
| 任务处理失败 | Error | `"导入任务失败：{JobId}"` |
| FailIngestionJob 失败 | Error | `"标记导入任务失败时出错：{JobId}"` |
| 轮询出错 | Error | `"导入工作器轮询出错"` |
| LLM 调用失败 | Error | `"LLM 调用失败：DocumentId={DocumentId}, RetryCount={RetryCount}, Error={Error}"` |

## 错误消息约定

### 用户可见错误（DocRetrievalValidationException）

| 场景 | 消息 |
|------|------|
| 文档名已存在 | `"文档名已存在"` |
| 文件已导入 | `"该文件已被导入"` |
| 文档不存在 | `"文档不存在"` |
| 文档未就绪 | `"文档未就绪，不允许修改元数据"` |
| 学科无效 | `"学科仅支持：英语"` |
| 年级无效 | `"年级取值非法，有效值：{ValidGrades}"` |
| 文档名为空 | `"文档名不能为空"` |
| 文档名过长 | `"文档名超过200字符"` |

### 系统内部错误

| 场景 | 异常类型 | 消息 |
|------|---------|------|
| 不支持的文件类型 | NotSupportedException | `"不支持的文件类型：{sourceType}"` |
| 文档不存在（Worker 中） | InvalidOperationException | `"文档不存在：{documentId}"` |
| Word 缺少 MainDocumentPart | InvalidOperationException | `"Word 文档缺少 MainDocumentPart"` |
| Word 缺少 Body | InvalidOperationException | `"Word 文档缺少 Body"` |
| PPT 缺少 PresentationPart | InvalidOperationException | `"PPT 文档缺少 PresentationPart"` |
| OpenSearch 查询失败 | InvalidOperationException | `"OpenSearch 查询失败，状态码：{statusCode}"` |
| LLM API 调用失败 | HttpRequestException | `"LLM API 调用失败：{provider}，状态码：{statusCode}"` |
| LLM 返回格式异常 | JsonException | `"LLM 返回格式异常：{response}"` |
| LLM 配置缺失 | InvalidOperationException | `"LLM 配置缺失：{configKey}"` |

## 代码风格

### 通用规则

- **C# 版本**：使用最新 C# 语法特性（集合表达式 `[]`、文件范围命名空间、`var` 推断）
- **命名空间**：文件范围命名空间 `namespace X.Y;`
- **缩进**：4 空格
- **行宽**：无硬性限制，但建议不超过 120 字符
- **空行**：方法之间 1 个空行，region 之间 1 个空行

### 依赖注入

```csharp
// 构造函数注入：必需依赖直接注入，可选依赖使用 nullable + 默认值 null
public IngestionWorker(
    IServiceProvider serviceProvider,          // 必需
    ILogger<IngestionWorker> logger,           // 必需
    ISearchIndexService? searchIndexService = null)  // 可选
```

### Scoped 服务获取

```csharp
// Worker 中通过 CreateScope 获取 Scoped 服务
using var scope = _serviceProvider.CreateScope();
var domainService = scope.ServiceProvider.GetRequiredService<DocumentDomainService>();
var parserService = scope.ServiceProvider.GetRequiredService<IDocumentParserService>();
```

### 异步模式

- 所有异步方法使用 `async/await`，不使用 `.Result` 或 `.Wait()`
- 异步方法名以 `Async` 结尾
- 始终传递 `CancellationToken`，在耗时操作前检查 `stoppingToken.ThrowIfCancellationRequested()`

### 错误处理模式

```csharp
// 任务级 try/catch：捕获异常后标记失败
try
{
    // 任务处理逻辑
}
catch (Exception ex)
{
    _logger.LogError(ex, "导入任务失败：{JobId}", job.Id);
    try
    {
        await domainService.FailIngestionJobAsync(job.Id, ex.Message);
    }
    catch (Exception failEx)
    {
        _logger.LogError(failEx, "标记导入任务失败时出错：{JobId}", job.Id);
    }
}

// 索引写入 try/catch：失败仅记日志
if (_searchIndexService != null)
{
    try
    {
        await _searchIndexService.IndexDocumentSegmentsAsync(...);
    }
    catch (Exception indexEx)
    {
        _logger.LogError(indexEx, "创建文档搜索索引失败：{DocumentId}", job.DocumentId);
    }
}
```

### LINQ 与集合操作

```csharp
// 使用 SelectMany 展平嵌套集合
var segmentModels = parsedDocument.Pages
    .SelectMany(p => p.Segments.Select(s => new DocumentSegmentModel { ... }))
    .ToList();

// 使用 ToDictionary 构建映射
var pageLookup = pageModels.ToDictionary(p => p.PageNumber, p => p.Id);
var segmentLookup = segmentModels.ToDictionary(s => s.SentenceId, s => s.Id);

// 使用 TryGetValue 安全查找
var pageId = pageLookup.TryGetValue(p.PageNumber, out var id) ? id : Guid.Empty;
```

### 模型初始化

```csharp
// 使用集合表达式初始化列表
public List<ParsedPage> Pages { get; set; } = [];
public List<ParsedSegment> Segments { get; set; } = [];

// 使用 Guid.NewGuid() 初始化 Id
public Guid Id { get; set; } = Guid.NewGuid();

// 使用 DateTimeOffset.UtcNow 初始化时间戳
public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

// 字符串属性使用 string.Empty 初始化
public string Text { get; set; } = string.Empty;
```

### 正则表达式

```csharp
// 使用 Source Generator 生成编译时正则，提升性能
[GeneratedRegex(@"^\s*(\d+)\s*[.、．)\]】]", RegexOptions.Compiled)]
private static partial Regex QuestionNumberRegex();

[GeneratedRegex(@"^\s*([A-Da-d])\s*[.、．)\]】]", RegexOptions.Compiled)]
private static partial Regex OptionRegex();
```

### Region 组织

```csharp
public partial class DocumentParserService : IDocumentParserService
{
    #region PDF 解析
    // PDF 相关方法
    #endregion

    #region Word 解析
    // Word 相关方法
    #endregion

    #region PPT 解析
    // PPT 相关方法
    #endregion

    #region OCR 后处理
    // OCR 后处理方法
    #endregion

    #region Token 分词与词干还原
    // 分词和词干还原方法
    #endregion

    #region 句子边界识别
    // 句子切分方法
    #endregion

    #region 题目边界识别
    // 题目提取方法
    #endregion
}
```

### LLM 分段代码模式

```csharp
// LLM 服务接口定义
public interface ILlmSegmentationService
{
    Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken = default);
    Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken = default);
}

// LLM 服务实现 - 调用外部 API
public class LlmSegmentationService : ILlmSegmentationService
{
    private readonly HttpClient _httpClient;
    private readonly LlmSegmentationOptions _options;
    private readonly ILogger<LlmSegmentationService> _logger;

    public async Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken)
    {
        var prompt = BuildAnalysisPrompt(textPreview);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseDocumentProfile(response);
    }

    public async Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken)
    {
        var prompt = BuildSegmentationPrompt(text, profile);
        var response = await CallLlmAsync(prompt, cancellationToken);
        return ParseSegmentResults(response);
    }

    private async Task<string> CallLlmAsync(string prompt, CancellationToken cancellationToken)
    {
        // 实现 LLM API 调用，支持重试和超时
    }
}

// DocumentParserService 集成 LLM 分段
public class DocumentParserService : IDocumentParserService
{
    private readonly ILlmSegmentationService? _llmSegmentation;

    // 构造函数注入，可选依赖
    public DocumentParserService(
        ILogger<DocumentParserService> logger,
        ILlmSegmentationService? llmSegmentation = null)
    {
        _logger = logger;
        _llmSegmentation = llmSegmentation;
    }

    private async Task<List<ParsedSegment>> SplitSentencesWithFallbackAsync(
        string blockText,
        DocumentProfile? profile,
        CancellationToken cancellationToken)
    {
        // 优先使用 LLM 分段
        if (_llmSegmentation != null && profile != null)
        {
            try
            {
                return await SplitSentencesWithLlmAsync(blockText, profile, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM 分段失败，回退到规则切割");
            }
        }

        // 回退到规则切割
        return SplitSentences(blockText);
    }
}
```

### LLM 配置模式

```csharp
// 配置选项类
public class LlmSegmentationOptions
{
    public const string SectionName = "LlmSegmentation";

    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ContextLength { get; set; } = string.Empty;  // "128K", "1M" etc.
    public int ContextLengthTokens { get; set; }               // parsed at startup
    public string MaxTokens { get; set; } = "4K";              // "4K", "128K" etc.
    public int MaxTokensValue { get; set; }                    // parsed at startup
    public int ChunkSize { get; set; }                         // computed at startup, capped at 5000
    public double Temperature => 0.1;
    public int MaxRetries => 3;
    public int TimeoutSeconds { get; set; } = 300;
}

// DI 注册
services.Configure<LlmSegmentationOptions>(
    configuration.GetSection(LlmSegmentationOptions.SectionName));

// 支持环境变量覆盖
// LLM_SEGMENTATION__PROVIDER=anthropic
// LLM_SEGMENTATION__API_KEY=sk-xxx
```

### 数据库表命名

- 表名：snake_case 复数形式，如 `document_ingestion_jobs`、`document_occurrences`
- 列名：snake_case，如 `document_id`、`page_number`、`start_offset`
- 外键列名：`{关联实体}_id`，如 `document_id`、`segment_id`、`question_segment_id`

### 状态值约定

| 实体 | 字段 | 有效值 |
|------|------|--------|
| Document | Status | `pending`、`processing`、`ready`、`failed` |
| DocumentIngestionJob | Status | `pending`、`processing`、`success`、`failed` |
| DocumentSegment | SegmentType | `sentence`、`concept`、`word_entry`、`question`、`knowledge_point` |
| DocumentOccurrence | — | SegmentId 和 QuestionSegmentId 互斥，一个为 null，另一个非 null |

### LLM 分段策略约定

| 学科 | 文档类型 | 分段策略 | 说明 |
|------|---------|---------|------|
| English | 教材 | sentence | 按完整句子分段 |
| English | 单词表 | word_entry | 每个词条为一个 segment |
| English | 短语表 | word_entry | 每个短语条目为一个 segment |
| English | 试卷 | question | 每道题为一个 segment |
| 语文 | 教材 | sentence | 按完整句子分段 |
| 数学 | 教材 | concept | 按知识点/公式分段 |
| 数学 | 试卷 | question | 每道题为一个 segment |
| 物理 | 教材 | concept | 按概念解释分段 |
| 化学 | 教材 | concept | 按概念解释分段 |
| 生物 | 教材 | concept | 按概念解释分段 |
| * | 知识点过关单 | knowledge_point | 每个知识点条目为一个 segment |
