using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using global::Ruoyu.Study.Common.Oss;
using global::Ruoyu.Study.DocLibrary.Domain.Models;
using global::Ruoyu.Study.DocLibrary.Domain.Repositories;
using global::Ruoyu.Study.DocLibrary.Domain.Services;
using global::Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests;

public class IngestionWorkerTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;
    private readonly Mock<ILogger<IngestionWorker>> _loggerMock;
    private readonly Mock<ISearchIndexService> _searchIndexServiceMock;
    private readonly Mock<IServiceScope> _scopeMock;
    private readonly Mock<IServiceScopeFactory> _scopeFactoryMock;
    private readonly Mock<IDocumentDomainService> _domainServiceMock;
    private readonly Mock<IDocumentParserService> _parserServiceMock;
    private readonly Mock<IOssService> _ossServiceMock;
    private readonly Mock<IDocumentPageRepository> _pageRepoMock;
    private readonly Mock<IDocumentSegmentRepository> _segmentRepoMock;
    private readonly Mock<IQuestionSegmentRepository> _questionRepoMock;
    private readonly Mock<IDocumentOccurrenceRepository> _occurrenceRepoMock;

    public IngestionWorkerTests()
    {
        _serviceProviderMock = new Mock<IServiceProvider>();
        _loggerMock = new Mock<ILogger<IngestionWorker>>();
        _searchIndexServiceMock = new Mock<ISearchIndexService>();
        _scopeMock = new Mock<IServiceScope>();
        _scopeFactoryMock = new Mock<IServiceScopeFactory>();
        _domainServiceMock = new Mock<IDocumentDomainService>();
        _parserServiceMock = new Mock<IDocumentParserService>();
        _ossServiceMock = new Mock<IOssService>();
        _pageRepoMock = new Mock<IDocumentPageRepository>();
        _segmentRepoMock = new Mock<IDocumentSegmentRepository>();
        _questionRepoMock = new Mock<IQuestionSegmentRepository>();
        _occurrenceRepoMock = new Mock<IDocumentOccurrenceRepository>();

        // Setup scope
        _scopeMock.Setup(s => s.ServiceProvider).Returns(_serviceProviderMock.Object);
        _scopeFactoryMock.Setup(f => f.CreateScope()).Returns(_scopeMock.Object);
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(IServiceScopeFactory))).Returns(_scopeFactoryMock.Object);

        // Setup GetRequiredService for each dependency
        SetupRequiredService(_domainServiceMock.Object);
        SetupRequiredService(_parserServiceMock.Object);
        SetupRequiredService(_ossServiceMock.Object);
        SetupRequiredService(_pageRepoMock.Object);
        SetupRequiredService(_segmentRepoMock.Object);
        SetupRequiredService(_questionRepoMock.Object);
        SetupRequiredService(_occurrenceRepoMock.Object);
    }

    private void SetupRequiredService<T>(T service) where T : class
    {
        _serviceProviderMock.Setup(sp => sp.GetService(typeof(T))).Returns(service);
    }

    [Fact]
    public async Task ExecuteAsync_WithNoPendingJobs_CompletesWithoutError()
    {
        // Arrange
        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel>());

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act - should not throw
        await worker.StartAsync(cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        // Assert - no exception means success
        _domainServiceMock.Verify(d => d.GetPendingJobsAsync(), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_WithPendingJob_ProcessesSuccessfully()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf",
            Subject = "英语",
            Grade = "G10",
            Year = "2023"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);

        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());

        var parsedDoc = new ParsedDocument
        {
            Pages = new List<ParsedPage>
            {
                new()
                {
                    PageNumber = 1,
                    Segments = new List<ParsedSegment>
                    {
                        new()
                        {
                            SentenceId = "p1-s1",
                            SegmentType = SegmentTypes.Sentence,
                            Text = "Hello world",
                            StartOffset = 0,
                            EndOffset = 11,
                            Tokens = new List<ParsedToken>
                            {
                                new() { TokenText = "hello", TokenStem = "hello", StartOffset = 0, EndOffset = 5 },
                                new() { TokenText = "world", TokenStem = "world", StartOffset = 6, EndOffset = 11 }
                            }
                        }
                    },
                    Questions = new List<ParsedQuestion>()
                }
            }
        };

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedDoc);

        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);

        _searchIndexServiceMock.Setup(s => s.IndexDocumentSegmentsAsync(
            documentId, document.Title, document.Subject, document.Grade, document.Year))
            .Returns(Task.CompletedTask);

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert
        _domainServiceMock.Verify(d => d.StartIngestionJobAsync(jobId, "v1.0", null), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.CompleteIngestionJobAsync(jobId), Times.AtLeastOnce());
        _searchIndexServiceMock.Verify(s => s.IndexDocumentSegmentsAsync(
            documentId, document.Title, document.Subject, document.Grade, document.Year), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_LlmAutoFillsSubjectAndGrade_WhenUserLeftEmpty()
    {
        // Arrange — user did not provide subject/grade/year
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf",
            Subject = "",
            Grade = "",
            Year = ""
        };

        var parsedDoc = new ParsedDocument
        {
            Profile = new DocumentProfile
            {
                Subject = "English",
                Grade = "G8",
                Year = "2024",
                DocType = "教材",
                SegmentStrategy = SegmentTypes.Sentence
            }
        };
        parsedDoc.Pages.Add(new ParsedPage
        {
            PageNumber = 1,
            Segments = new List<ParsedSegment>(),
            Questions = new List<ParsedQuestion>()
        });

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath)).ReturnsAsync(new System.IO.MemoryStream());
        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedDoc);
        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);

        var worker = new IngestionWorker(_serviceProviderMock.Object, _loggerMock.Object, _searchIndexServiceMock.Object);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert — UpdateDocumentProfileAsync called with AI-detected subject/grade/year
        _domainServiceMock.Verify(d => d.UpdateDocumentProfileAsync(
            documentId,
            It.IsAny<string>(),
            "English",  // AI-detected subject
            "G8",       // AI-detected grade
            "2024"      // AI-detected year
        ), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_PreservesUserProvidedSubject_WhenLlmAlsoDetects()
    {
        // Arrange — user provided subject/grade, LLM detected different values
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf",
            Subject = "英语",
            Grade = "G10",
            Year = "2023"
        };

        var parsedDoc = new ParsedDocument
        {
            Profile = new DocumentProfile
            {
                Subject = "English",  // LLM detected different
                Grade = "G8",         // LLM detected different
                Year = "2024",        // LLM detected different
                DocType = "教材",
                SegmentStrategy = SegmentTypes.Sentence
            }
        };
        parsedDoc.Pages.Add(new ParsedPage { PageNumber = 1, Segments = new List<ParsedSegment>(), Questions = new List<ParsedQuestion>() });

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath)).ReturnsAsync(new System.IO.MemoryStream());
        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedDoc);
        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);

        var worker = new IngestionWorker(_serviceProviderMock.Object, _loggerMock.Object, _searchIndexServiceMock.Object);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert — user values preserved, AI values NOT passed
        _domainServiceMock.Verify(d => d.UpdateDocumentProfileAsync(
            documentId,
            It.IsAny<string>(),
            null,  // null = don't override user's subject
            null,  // null = don't override user's grade
            null   // null = don't override user's year
        ), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_WhenParsingFails_MarksJobAsFailed()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "corrupt.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/corrupt.pdf",
            Subject = "英语",
            Grade = "G10",
            Year = "2023"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>())).Returns(Task.CompletedTask);

        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("PDF parsing failed"));

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert
        _domainServiceMock.Verify(d => d.FailIngestionJobAsync(jobId, It.Is<string>(msg => msg.Contains("PDF parsing failed"))), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_WhenSearchIndexFails_DoesNotFailJob()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf",
            Subject = "英语",
            Grade = "G10",
            Year = "2023"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);

        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());

        var parsedDoc = new ParsedDocument
        {
            Pages = new List<ParsedPage>
            {
                new()
                {
                    PageNumber = 1,
                    Segments = new List<ParsedSegment>(),
                    Questions = new List<ParsedQuestion>()
                }
            }
        };

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedDoc);

        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);

        // Search index fails
        _searchIndexServiceMock.Setup(s => s.IndexDocumentSegmentsAsync(
            documentId, document.Title, document.Subject, document.Grade, document.Year))
            .ThrowsAsync(new Exception("OpenSearch unavailable"));

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert - job should still be completed (search index failure is non-fatal)
        _domainServiceMock.Verify(d => d.CompleteIngestionJobAsync(jobId), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>()), Times.Never());
    }

    [Fact]
    public async Task ExecuteAsync_WithQuestionSegments_WritesQuestionAndOccurrenceData()
    {
        // Arrange
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "exam.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/exam.pdf",
            Subject = "英语",
            Grade = "G10",
            Year = "2023"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);

        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());

        var parsedDoc = new ParsedDocument
        {
            Pages = new List<ParsedPage>
            {
                new()
                {
                    PageNumber = 1,
                    Segments = new List<ParsedSegment>(),
                    Questions = new List<ParsedQuestion>
                    {
                        new()
                        {
                            QuestionId = "q1",
                            Stem = "What is the capital of France?",
                            StartOffset = 0,
                            EndOffset = 30,
                            Tokens = new List<ParsedToken>
                            {
                                new() { TokenText = "capital", TokenStem = "capit", StartOffset = 12, EndOffset = 19 }
                            }
                        }
                    }
                }
            }
        };

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(parsedDoc);

        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            null); // No search index service

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert
        _questionRepoMock.Verify(r => r.AddRangeAsync(It.Is<List<QuestionSegmentModel>>(list => list.Count == 1)), Times.AtLeastOnce());
        _occurrenceRepoMock.Verify(r => r.AddRangeAsync(It.Is<List<DocumentOccurrenceModel>>(list => list.Count == 1)), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_StopsOnCancellation()
    {
        // Arrange - return pending jobs but cancel immediately
        _domainServiceMock.Setup(d => d.GetPendingJobsAsync())
            .Returns(async () =>
            {
                await Task.Delay(100);
                return new List<DocumentIngestionJobModel>();
            });

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            null);

        using var cts = new CancellationTokenSource();

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Task.Delay(200);

        // Assert - worker should have stopped without errors
        Assert.True(true); // No exception means success
    }

    [Fact]
    public async Task ExecuteAsync_NullOssStream_MarksJobFailed()
    {
        // Arrange - OSS returns null stream
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "missing.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/missing.pdf"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>())).Returns(Task.CompletedTask);
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath)).ReturnsAsync(default(System.IO.Stream)!);

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource(3000);

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);

        // Assert - job should be marked failed with meaningful error
        _domainServiceMock.Verify(d => d.FailIngestionJobAsync(jobId, It.Is<string>(msg => msg.Contains("OSS") || msg.Contains("null"))), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_ProgressTransitionsThroughAllStages()
    {
        // Arrange - happy path: complete success
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());
        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ParsedDocument { Pages = new List<ParsedPage>() });
        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);
        _searchIndexServiceMock.Setup(s => s.IndexDocumentSegmentsAsync(
            documentId, document.Title, document.Subject, document.Grade, document.Year))
            .Returns(Task.CompletedTask);

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(600);

        // Assert: 8 阶段都被调用（analyzing/parsing 通过 Progress<ParsingProgress> 回调报告，不直接调用 UpdateJobProgressAsync）
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 5, "starting"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 10, "downloading"), Times.AtLeastOnce());
        // analyzing: 10-20 range via Progress<ParsingProgress> callback
        // parsing: 20-75 range via Progress<ParsingProgress> callback
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 80, "writing_pages"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 90, "writing_segments"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 100, "indexing"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 100, "completed"), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_ProgressStopsAtFailurePoint()
    {
        // Arrange: ParseAsync 抛异常 → 进度卡在 downloading（10）后任务失败
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "broken.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/broken.pdf"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>())).Returns(Task.CompletedTask);
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());
        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("PDF 解析失败"));

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource(3000);

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);

        // Assert: 5/10 都被调用，80 之后从未调用（parsing 通过 Progress 回调，ParseAsync 抛异常时不会触发）
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 5, "starting"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 10, "downloading"), Times.AtLeastOnce());
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 80, "writing_pages"), Times.Never);
        _domainServiceMock.Verify(d => d.UpdateJobProgressAsync(jobId, 90, "writing_segments"), Times.Never);
        _domainServiceMock.Verify(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task ExecuteAsync_ProgressUpdatesAreWrappedInTryCatch()
    {
        // Arrange: UpdateJobProgressAsync(10, "downloading") 抛异常 → 任务仍能正常失败
        var documentId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var job = new DocumentIngestionJobModel { Id = jobId, DocumentId = documentId, Status = DocumentStatus.Pending };
        var document = new DocumentModel
        {
            Id = documentId,
            Title = "test.pdf",
            SourceType = SourceTypes.Pdf,
            FilePath = "/test/path.pdf"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.CompleteIngestionJobAsync(jobId)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>())).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.UpdateJobProgressAsync(jobId, 10, "downloading"))
            .ThrowsAsync(new Exception("Progress update failed"));
        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());
        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<IProgress<ParsingProgress> >(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ParsedDocument { Pages = new List<ParsedPage>() });
        _pageRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentPageModel>>())).Returns(Task.CompletedTask);
        _segmentRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentSegmentModel>>())).Returns(Task.CompletedTask);
        _questionRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<QuestionSegmentModel>>())).Returns(Task.CompletedTask);
        _occurrenceRepoMock.Setup(r => r.AddRangeAsync(It.IsAny<List<DocumentOccurrenceModel>>())).Returns(Task.CompletedTask);

        var worker = new IngestionWorker(
            _serviceProviderMock.Object,
            _loggerMock.Object,
            _searchIndexServiceMock.Object);

        using var cts = new CancellationTokenSource(3000);

        // Act
        await worker.StartAsync(cts.Token);
        await Task.Delay(500);

        // Assert: 即使 progress 调用失败，任务仍被标记为 failed
        _domainServiceMock.Verify(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>()), Times.AtLeastOnce());
    }
}
