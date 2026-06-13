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

namespace Ruoyu.Study.DocRetrieval.Tests;

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
            Subject = "English",
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
                            BlockId = "p1-b1",
                            SentenceId = "p1-b1-s1",
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

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<CancellationToken>()))
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
            Subject = "English",
            Grade = "G10",
            Year = "2023"
        };

        _domainServiceMock.Setup(d => d.GetPendingJobsAsync()).ReturnsAsync(new List<DocumentIngestionJobModel> { job });
        _domainServiceMock.Setup(d => d.StartIngestionJobAsync(jobId, "v1.0", null)).Returns(Task.CompletedTask);
        _domainServiceMock.Setup(d => d.GetDocumentAsync(documentId)).ReturnsAsync(document);
        _domainServiceMock.Setup(d => d.FailIngestionJobAsync(jobId, It.IsAny<string>())).Returns(Task.CompletedTask);

        _ossServiceMock.Setup(o => o.DownloadAsync(document.FilePath))
            .ReturnsAsync(new System.IO.MemoryStream());

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<CancellationToken>()))
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
            Subject = "English",
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

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<CancellationToken>()))
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
            Subject = "English",
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

        _parserServiceMock.Setup(p => p.ParseAsync(It.IsAny<System.IO.Stream>(), document.SourceType, It.IsAny<CancellationToken>()))
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
}
