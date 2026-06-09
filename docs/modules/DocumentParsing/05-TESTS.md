# 05-TESTS — DocumentParsing 测试文档

## 单元测试

### IngestionWorker 编排逻辑测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-W-01 | 轮询间隔为 10 秒 | REQ-PARSE-01 | 创建 IngestionWorker 实例 | `_pollInterval == TimeSpan.FromSeconds(10)` |
| UT-W-02 | 获取到 pending 任务后调用 StartIngestionJobAsync | REQ-PARSE-02 | GetPendingJobsAsync 返回 1 个任务 | StartIngestionJobAsync 被调用 1 次，参数 `(jobId, "v1.0", null)` |
| UT-W-03 | 解析成功后调用 CompleteIngestionJobAsync | REQ-PARSE-02 | ParseAsync 正常返回 | CompleteIngestionJobAsync 被调用 1 次 |
| UT-W-04 | 解析失败后调用 FailIngestionJobAsync | REQ-PARSE-09 | ParseAsync 抛出异常 | FailIngestionJobAsync 被调用，errorMessage = ex.Message |
| UT-W-05 | 单个任务失败不影响后续任务 | REQ-PARSE-09 | 2 个 pending 任务，第 1 个解析失败 | 第 1 个调用 FailIngestionJobAsync，第 2 个正常完成 |
| UT-W-06 | PageNumber → PageId 映射正确 | REQ-PARSE-04 | ParsedDocument 含 2 页 | segment 和 question 的 PageId 与 pageLookup 对应 |
| UT-W-07 | SentenceId → SegmentId 映射正确 | REQ-PARSE-06 | ParsedDocument 含 segments | occurrence 的 SegmentId 与 segmentLookup 对应 |
| UT-W-08 | QuestionId → QuestionSegmentId 映射正确 | REQ-PARSE-06 | ParsedDocument 含 questions | occurrence 的 QuestionSegmentId 与 questionLookup 对应 |
| UT-W-09 | 搜索索引失败不影响任务状态 | REQ-PARSE-07 | IndexDocumentSegmentsAsync 抛异常 | 任务仍为 success，日志记录错误 |
| UT-W-10 | 向量索引失败不影响任务状态 | REQ-PARSE-08 | IndexDocumentVectorsAsync 抛异常 | 任务仍为 success，日志记录错误 |
| UT-W-11 | 搜索索引为 null 时跳过索引写入 | REQ-PARSE-07 | _searchIndexService = null | IndexDocumentSegmentsAsync 不被调用 |
| UT-W-12 | 向量索引为 null 时跳过索引写入 | REQ-PARSE-08 | _qdrantService = null | IndexDocumentVectorsAsync 不被调用 |
| UT-W-13 | FailIngestionJobAsync 自身失败时记录日志 | REQ-PARSE-09 | FailIngestionJobAsync 抛异常 | 记录 "标记导入任务失败时出错" 日志 |
| UT-W-14 | 文档不存在时任务标记 failed | REQ-PARSE-09 | GetDocumentAsync 返回 null | 抛出 InvalidOperationException，任务标记 failed |

### DocumentDomainService 任务状态管理测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-D-01 | StartIngestionJobAsync 设置 processing | REQ-PARSE-02 | 存在 pending 任务 | job.Status = "processing"，document.Status = "processing"，StartedAt 不为 null |
| UT-D-02 | CompleteIngestionJobAsync 设置 success | REQ-PARSE-02 | 存在 processing 任务 | job.Status = "success"，document.Status = "ready"，FinishedAt 不为 null |
| UT-D-03 | FailIngestionJobAsync 设置 failed | REQ-PARSE-02 | 存在 processing 任务 | job.Status = "failed"，document.Status = "failed"，ErrorMessage 不为空 |
| UT-D-04 | StartIngestionJobAsync 记录 ParserVersion | REQ-PARSE-02 | parserVersion = "v1.0" | job.ParserVersion = "v1.0" |
| UT-D-05 | GetPendingJobsAsync 返回 pending 任务 | REQ-PARSE-01 | 数据库有 2 个 pending 任务 | 返回 2 个任务 |
| UT-D-06 | StartIngestionJobAsync 对不存在的 job 静默返回 | REQ-PARSE-02 | jobId 不存在 | 不抛异常 |

### DocumentParserService 解析逻辑测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| UT-P-01 | PDF 解析返回正确页数 | REQ-PARSE-03 | 3 页 PDF 文件流 | ParsedDocument.Pages.Count == 3 |
| UT-P-02 | PDF 空白页生成空 ParsedPage | REQ-PARSE-03 | PDF 某页无文本 | 该页 Segments 和 Questions 为空 |
| UT-P-03 | Word 解析视为单页 | REQ-PARSE-03 | DOCX 文件流 | ParsedDocument.Pages.Count == 1，PageNumber == 1 |
| UT-P-04 | PPT 每页幻灯片对应一个 ParsedPage | REQ-PARSE-03 | 5 页 PPTX | ParsedDocument.Pages.Count == 5 |
| UT-P-05 | 不支持的文件类型抛出 NotSupportedException | REQ-PARSE-03 | sourceType = "xlsx" | 抛出 NotSupportedException |
| UT-P-06 | SentenceId 格式为 {blockId}-s{index} | REQ-PARSE-03 | 解析含多句的块 | SentenceId 如 "p1-b1-s1"、"p1-b1-s2" |
| UT-P-07 | BlockId 格式为 p{page}-b{index} | REQ-PARSE-03 | 解析含多块的页 | BlockId 如 "p1-b1"、"p1-b2" |
| UT-P-08 | Token 分词只保留英文单词 | REQ-PARSE-03 | 文本含数字和标点 | Tokens 仅含字母组成的单词 |
| UT-P-09 | Porter 词干还原正确 | REQ-PARSE-03 | TokenText = "running" | TokenStem = "run" |
| UT-P-10 | 句子边界识别排除缩写 | REQ-PARSE-03 | 文本含 "Mr. Smith" | 不在 "Mr." 后切分句子 |
| UT-P-11 | 题目边界识别正确 | REQ-PARSE-03 | 文本含 "1. What is..." | 生成 ParsedQuestion，QuestionId = "q1" |
| UT-P-12 | OCR 后处理合并连字符打断的单词 | REQ-PARSE-03 | 文本含 "word-\nword" | 合并为 "wordword" |

## 集成测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| IT-01 | 完整 PDF 解析流程 | REQ-PARSE-01~09 | 上传 PDF 文档 | pages/segments/questions/occurrences 全部写入，任务 success |
| IT-02 | 完整 Word 解析流程 | REQ-PARSE-01~09 | 上传 DOCX 文档 | 同上 |
| IT-03 | 完整 PPT 解析流程 | REQ-PARSE-01~09 | 上传 PPTX 文档 | 同上 |
| IT-04 | 搜索索引写入验证 | REQ-PARSE-07 | 解析完成 | OpenSearch 中可查到文档 segments |
| IT-05 | 向量索引写入验证 | REQ-PARSE-08 | 解析完成 | Qdrant 中可查到文档向量 |

## 边界测试

| # | 测试用例 | 覆盖需求 | 前置条件 | 预期结果 |
|---|---------|---------|---------|---------|
| BT-01 | 空文档（0 页） | REQ-PARSE-03 | 空文件流 | 解析成功，Pages 为空，任务 success |
| BT-02 | 无 Token 的文档 | REQ-PARSE-06 | 文档仅含数字/符号 | occurrenceModels 为空，不调用 AddRangeAsync |
| BT-03 | 无 Segment 的页面 | REQ-PARSE-05 | PDF 页仅含图片无文本 | ParsedPage.Segments 为空，写入 0 条 segment |
| BT-04 | 无 Question 的文档 | REQ-PARSE-05 | 文档无题目格式 | ParsedPage.Questions 为空，写入 0 条 question |
| BT-05 | 超大文档（100+ 页） | 非功能需求 | 100 页 PDF | 解析成功，所有映射正确 |
| BT-06 | 取消令牌在解析中触发 | REQ-PARSE-01 | 解析过程中取消 | 抛出 OperationCanceledException |

## 骨架代码

### IngestionWorkerTests.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests.Service;

public class IngestionWorkerTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<ILogger<IngestionWorker>> _loggerMock;
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly Mock<IQdrantService> _qdrantServiceMock;

    public IngestionWorkerTests()
    {
        _serviceProviderMock = new Mock<IServiceProvider>();
        _loggerMock = new Mock<ILogger<IngestionWorker>>();
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        _qdrantServiceMock = new Mock<IQdrantService>();
    }

    private IngestionWorker CreateWorker(
        ISearchIndexService? searchIndexService = null,
        IQdrantService? qdrantService = null)
    {
        return new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            searchIndexService,
            qdrantService);
    }

    [Fact]
    public void PollInterval_ShouldBe10Seconds()
    {
        // UT-W-01: 验证轮询间隔为 10 秒
        var worker = CreateWorker();
        // 通过反射或公开属性验证 _pollInterval
        // Assert.Equal(TimeSpan.FromSeconds(10), worker.PollInterval);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCallStartIngestionJobAsync_WhenPendingJobExists()
    {
        // UT-W-02: 获取到 pending 任务后调用 StartIngestionJobAsync
        var cts = new CancellationTokenSource();
        var domainServiceMock = new Mock<DocumentDomainService>();
        var job = new DocumentIngestionJobModel
        {
            Id = Guid.NewGuid(),
            DocumentId = Guid.NewGuid(),
            Status = "pending"
        };

        domainServiceMock.Setup(d => d.GetPendingJobsAsync())
            .ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        domainServiceMock.Setup(d => d.GetDocumentAsync(job.DocumentId))
            .ReturnsAsync((DocumentModel?)null);

        // 设置 scope 解析...
        // 验证 StartIngestionJobAsync 被调用
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCallFailIngestionJobAsync_WhenParseFails()
    {
        // UT-W-04: 解析失败后调用 FailIngestionJobAsync
        var domainServiceMock = new Mock<DocumentDomainService>();
        var parserServiceMock = new Mock<IDocumentParserService>();
        parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("解析失败"));

        // 验证 FailIngestionJobAsync 被调用
    }

    [Fact]
    public async Task ExecuteAsync_SearchIndexFailure_ShouldNotAffectJobStatus()
    {
        // UT-W-09: 搜索索引失败不影响任务状态
        _searchIndexServiceMock.Setup(s => s.IndexDocumentSegmentsAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("OpenSearch 不可用"));

        // 验证 CompleteIngestionJobAsync 仍被调用
    }

    [Fact]
    public async Task ExecuteAsync_VectorIndexFailure_ShouldNotAffectJobStatus()
    {
        // UT-W-10: 向量索引失败不影响任务状态
        _qdrantServiceMock.Setup(q => q.IndexDocumentVectorsAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Qdrant 不可用"));

        // 验证 CompleteIngestionJobAsync 仍被调用
    }
}
```

### DocumentDomainServiceTests.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests.Domain;

public class DocumentDomainServiceTests
{
    private readonly Mock<IDocumentRepository> _documentRepoMock;
    private readonly Mock<IDocumentIngestionJobRepository> _jobRepoMock;
    private readonly Mock<IUnitOfWork> _unitOfWorkMock;
    private readonly Mock<ILogger<DocumentDomainService>> _loggerMock;
    private readonly DocumentDomainService _service;

    public DocumentDomainServiceTests()
    {
        _documentRepoMock = new Mock<IDocumentRepository>();
        _jobRepoMock = new Mock<IDocumentIngestionJobRepository>();
        _unitOfWorkMock = new Mock<IUnitOfWork>();
        _loggerMock = new Mock<ILogger<DocumentDomainService>>();

        _service = new DocumentDomainService(
            _documentRepoMock.Object,
            Mock.Of<IDocumentPageRepository>(),
            Mock.Of<IDocumentSegmentRepository>(),
            Mock.Of<IQuestionSegmentRepository>(),
            Mock.Of<IDocumentOccurrenceRepository>(),
            _jobRepoMock.Object,
            _unitOfWorkMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task StartIngestionJobAsync_ShouldSetProcessingStatus()
    {
        // UT-D-01: StartIngestionJobAsync 设置 processing
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "pending" };
        var document = new DocumentModel { Id = documentId, Status = "pending" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.StartIngestionJobAsync(jobId, "v1.0", null);

        Assert.Equal("processing", job.Status);
        Assert.NotNull(job.StartedAt);
        Assert.Equal("v1.0", job.ParserVersion);
        Assert.Equal("processing", document.Status);
    }

    [Fact]
    public async Task CompleteIngestionJobAsync_ShouldSetSuccessStatus()
    {
        // UT-D-02: CompleteIngestionJobAsync 设置 success
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.CompleteIngestionJobAsync(jobId);

        Assert.Equal("success", job.Status);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("ready", document.Status);
    }

    [Fact]
    public async Task FailIngestionJobAsync_ShouldSetFailedStatus()
    {
        // UT-D-03: FailIngestionJobAsync 设置 failed
        var jobId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = "processing" };
        var document = new DocumentModel { Id = documentId, Status = "processing" };

        _jobRepoMock.Setup(r => r.GetByIdAsync(jobId)).ReturnsAsync(job);
        _documentRepoMock.Setup(r => r.GetByIdAsync(documentId)).ReturnsAsync(document);

        await _service.FailIngestionJobAsync(jobId, "解析失败");

        Assert.Equal("failed", job.Status);
        Assert.Equal("解析失败", job.ErrorMessage);
        Assert.NotNull(job.FinishedAt);
        Assert.Equal("failed", document.Status);
    }
}
```

### DocumentParserServiceTests.cs

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Ruoyu.Study.DocRetrieval.Service;
using Xunit;

namespace Ruoyu.Study.DocRetrieval.Tests.Service;

public class DocumentParserServiceTests
{
    private readonly DocumentParserService _service;

    public DocumentParserServiceTests()
    {
        var loggerMock = new Mock<ILogger<DocumentParserService>>();
        _service = new DocumentParserService(loggerMock.Object);
    }

    [Fact]
    public async Task ParseAsync_UnsupportedType_ShouldThrowNotSupportedException()
    {
        // UT-P-05: 不支持的文件类型抛出 NotSupportedException
        using var stream = new MemoryStream();
        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => _service.ParseAsync(stream, "xlsx", CancellationToken.None));
        Assert.Contains("不支持的文件类型", ex.Message);
    }

    // UT-P-08: Token 分词只保留英文单词
    [Fact]
    public void Tokenize_ShouldOnlyKeepEnglishWords()
    {
        // 通过反射调用 Tokenize 方法，或通过 ParseAsync 间接测试
        // 输入: "Hello 123 World!"
        // 预期: Tokens 包含 "Hello" 和 "World"，不包含 "123"
    }

    // UT-P-09: Porter 词干还原正确
    [Fact]
    public void PorterStem_Running_ShouldReturnRun()
    {
        // 通过反射调用 PorterStem 方法
        // 输入: "running"
        // 预期: "run"
    }

    // UT-P-10: 句子边界识别排除缩写
    [Fact]
    public void SplitSentences_ShouldNotSplitOnAbbreviations()
    {
        // 输入: "Mr. Smith went to Dr. Jones."
        // 预期: 不在 "Mr." 或 "Dr." 后切分
    }
}
```
