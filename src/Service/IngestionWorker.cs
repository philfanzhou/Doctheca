using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly ISearchIndexService? _searchIndexService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public IngestionWorker(IServiceProvider serviceProvider, ILogger<IngestionWorker> logger, ISearchIndexService? searchIndexService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _searchIndexService = searchIndexService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Document ingestion background worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var domainService = scope.ServiceProvider.GetRequiredService<IDocumentDomainService>();
                var parserService = scope.ServiceProvider.GetRequiredService<IDocumentParserService>();
                var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();
                var pageRepository = scope.ServiceProvider.GetRequiredService<IDocumentPageRepository>();
                var segmentRepository = scope.ServiceProvider.GetRequiredService<IDocumentSegmentRepository>();
                var questionRepository = scope.ServiceProvider.GetRequiredService<IQuestionSegmentRepository>();
                var occurrenceRepository = scope.ServiceProvider.GetRequiredService<IDocumentOccurrenceRepository>();

                var pendingJobs = await domainService.GetPendingJobsAsync();

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await domainService.StartIngestionJobAsync(job.Id, "v1.0", null);
                        _logger.LogInformation("Starting ingestion job: {JobId}, document: {DocumentId}", job.Id, job.DocumentId);

                        // Get document info
                        var document = await domainService.GetDocumentAsync(job.DocumentId)
                            ?? throw new InvalidOperationException($"Document not found: {job.DocumentId}");

                        // Download file stream from OSS
                        using var fileStream = await ossService.DownloadAsync(document.FilePath);

                        // Call document parsing service
                        var parsedDocument = await parserService.ParseAsync(fileStream, document.SourceType, stoppingToken);
                        _logger.LogInformation("Document parsing completed: {DocumentId}, {PageCount} pages", job.DocumentId, parsedDocument.Pages.Count);

                        // Write parsing results to database
                        var pageModels = parsedDocument.Pages.Select(p => new DocumentPageModel
                        {
                            Id = Guid.NewGuid(),
                            DocumentId = document.Id,
                            PageNumber = p.PageNumber,
                            CreatedAt = DateTimeOffset.UtcNow
                        }).ToList();

                        await pageRepository.AddRangeAsync(pageModels);

                        // Build PageNumber -> PageId mapping
                        var pageLookup = pageModels.ToDictionary(p => p.PageNumber, p => p.Id);

                        var segmentModels = parsedDocument.Pages
                            .SelectMany(p => p.Segments.Select(s => new DocumentSegmentModel
                            {
                                Id = Guid.NewGuid(),
                                DocumentId = document.Id,
                                PageId = pageLookup.TryGetValue(p.PageNumber, out var pageId) ? pageId : Guid.Empty,
                                BlockId = s.BlockId,
                                SentenceId = s.SentenceId,
                                SegmentType = s.SegmentType,
                                Text = s.Text,
                                StartOffset = s.StartOffset,
                                EndOffset = s.EndOffset,
                                CreatedAt = DateTimeOffset.UtcNow
                            }))
                            .ToList();

                        await segmentRepository.AddRangeAsync(segmentModels);

                        var questionModels = parsedDocument.Pages
                            .SelectMany(p => p.Questions.Select(q => new QuestionSegmentModel
                            {
                                Id = Guid.NewGuid(),
                                DocumentId = document.Id,
                                PageId = pageLookup.TryGetValue(p.PageNumber, out var pageId) ? pageId : Guid.Empty,
                                QuestionId = q.QuestionId,
                                Stem = q.Stem,
                                OptionsJson = q.OptionsJson,
                                AnswerArea = q.AnswerArea,
                                StartOffset = q.StartOffset,
                                EndOffset = q.EndOffset,
                                CreatedAt = DateTimeOffset.UtcNow
                            }))
                            .ToList();

                        await questionRepository.AddRangeAsync(questionModels);

                        // Write tokens to document_occurrences table
                        var occurrenceModels = new List<DocumentOccurrenceModel>();

                        // Build SentenceId -> SegmentId mapping
                        var segmentLookup = segmentModels.ToDictionary(s => s.SentenceId, s => s.Id);

                        foreach (var page in parsedDocument.Pages)
                        {
                            // Generate occurrences from segment tokens
                            foreach (var seg in page.Segments)
                            {
                                var segmentId = segmentLookup.TryGetValue(seg.SentenceId, out var sid) ? sid : (Guid?)null;
                                foreach (var token in seg.Tokens)
                                {
                                    occurrenceModels.Add(new DocumentOccurrenceModel
                                    {
                                        Id = Guid.NewGuid(),
                                        DocumentId = document.Id,
                                        SegmentId = segmentId,
                                        QuestionSegmentId = null,
                                        TokenText = token.TokenText,
                                        TokenStem = token.TokenStem,
                                        StartOffset = token.StartOffset,
                                        EndOffset = token.EndOffset,
                                        CreatedAt = DateTimeOffset.UtcNow
                                    });
                                }
                            }

                            // Generate occurrences from question tokens
                            var questionLookup = questionModels
                                .Where(qm => page.Questions.Any(pq => pq.QuestionId == qm.QuestionId))
                                .ToDictionary(qm => qm.QuestionId, qm => qm.Id);

                            foreach (var question in page.Questions)
                            {
                                var questionSegmentId = questionLookup.TryGetValue(question.QuestionId, out var qid) ? qid : (Guid?)null;
                                foreach (var token in question.Tokens)
                                {
                                    occurrenceModels.Add(new DocumentOccurrenceModel
                                    {
                                        Id = Guid.NewGuid(),
                                        DocumentId = document.Id,
                                        SegmentId = null,
                                        QuestionSegmentId = questionSegmentId,
                                        TokenText = token.TokenText,
                                        TokenStem = token.TokenStem,
                                        StartOffset = token.StartOffset,
                                        EndOffset = token.EndOffset,
                                        CreatedAt = DateTimeOffset.UtcNow
                                    });
                                }
                            }
                        }

                        if (occurrenceModels.Count > 0)
                        {
                            await occurrenceRepository.AddRangeAsync(occurrenceModels);
                            _logger.LogInformation("Token write completed: {DocumentId}, {TokenCount} tokens", job.DocumentId, occurrenceModels.Count);
                        }

                        // Mark ingestion job as completed
                        await domainService.CompleteIngestionJobAsync(job.Id);
                        _logger.LogInformation("Ingestion job completed: {JobId}, {SegmentCount} segments, {QuestionCount} questions",
                            job.Id, segmentModels.Count, questionModels.Count);

                        // After ingestion, write document segments to search index
                        if (_searchIndexService != null)
                        {
                            try
                            {
                                await _searchIndexService.IndexDocumentSegmentsAsync(
                                    document.Id, document.Title, document.Subject, document.Grade, document.Year);
                                _logger.LogInformation("Document search index created: {DocumentId}", job.DocumentId);
                            }
                            catch (Exception indexEx)
                            {
                                _logger.LogError(indexEx, "Failed to create document search index: {DocumentId}", job.DocumentId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Ingestion job failed: {JobId}", job.Id);
                        try
                        {
                            await domainService.FailIngestionJobAsync(job.Id, ex.Message);
                        }
                        catch (Exception failEx)
                        {
                            _logger.LogError(failEx, "Error marking ingestion job as failed: {JobId}", job.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion worker polling error");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
