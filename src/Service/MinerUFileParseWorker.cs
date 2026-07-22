using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service.Parsing;

namespace Ruoyu.Study.DocLibrary.Service;

public sealed class MinerUFileParseWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MinerUFileParseWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public MinerUFileParseWorker(IServiceProvider serviceProvider, ILogger<MinerUFileParseWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MinerU file parse worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var parseService = scope.ServiceProvider.GetRequiredService<IDocumentParseService>();
                var fileService = scope.ServiceProvider.GetRequiredService<IDocumentFileService>();
                var orchestrator = scope.ServiceProvider.GetRequiredService<MinerUParseOrchestrator>();

                var pendingJobs = await parseService.GetPendingJobsAsync();

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        var markdown = await orchestrator.ProcessFileAsync(job, stoppingToken);

                        if (markdown is not null)
                        {
                            var file = await fileService.GetByIdAsync(job.DocumentFileId)
                                ?? throw new InvalidOperationException($"Document file not found: {job.DocumentFileId}");

                            await IndexBlocksToSearchAsync(job.Id, file, scope.ServiceProvider);
                            await AnalyzeMetadataIfMissingAsync(job.DocumentFileId, markdown, scope.ServiceProvider, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "MinerU parse failed for parse {ParseId}", job.Id);
                        try
                        {
                            await parseService.UpdateStatusAsync(job.Id, DocumentParseStatus.Failed, ex.Message);
                        }
                        catch (Exception failEx)
                        {
                            _logger.LogError(failEx, "Error marking parse as failed: {ParseId}", job.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — rethrow to exit the while loop
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MinerU file parse worker polling error");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Index parse blocks to OpenSearch. Best-effort: failures are logged but do not block the parse workflow.
    /// Passes the file's existing subject/grade/year so that indexing metadata can be filtered.
    /// </summary>
    private async Task IndexBlocksToSearchAsync(Guid parseId, DocumentFileModel file, IServiceProvider scopeProvider)
    {
        try
        {
            var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
            await searchIndexService.IndexParseBlocksAsync(parseId, file.Id, file.FileName, file.Subject, file.Grade, file.Year);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index parse {ParseId} to OpenSearch", parseId);
        }
    }

    /// <summary>
    /// Analyze document metadata (subject/grade/year) via LLM if any field is missing.
    /// Best-effort: failures are logged but do not block the parse workflow.
    /// Skips if all three fields are already set, or if LLM is not configured.
    /// </summary>
    private async Task AnalyzeMetadataIfMissingAsync(
        Guid documentFileId,
        string? markdownContent,
        IServiceProvider scopeProvider,
        CancellationToken ct)
    {
        try
        {
            var fileService = scopeProvider.GetRequiredService<IDocumentFileService>();
            var file = await fileService.GetByIdAsync(documentFileId);
            if (file == null)
            {
                _logger.LogWarning("Document file not found for metadata analysis: {FileId}", documentFileId);
                return;
            }

            // Skip if all metadata fields are already set (by human or previous LLM analysis)
            if (!string.IsNullOrWhiteSpace(file.Subject)
                && !string.IsNullOrWhiteSpace(file.Grade)
                && !string.IsNullOrWhiteSpace(file.Year))
            {
                _logger.LogInformation(
                    "Metadata already set for file {FileId}, skip LLM analysis (Subject={Subject}, Grade={Grade}, Year={Year})",
                    documentFileId, file.Subject, file.Grade, file.Year);
                return;
            }

            // LLM document analysis service is optional (registered only when ApiKey is configured)
            var llmService = scopeProvider.GetService<IDocumentAnalysisService>();
            if (llmService == null)
            {
                _logger.LogInformation("LLM not configured, skip metadata analysis for file {FileId}", documentFileId);
                return;
            }

            if (string.IsNullOrWhiteSpace(markdownContent))
            {
                _logger.LogInformation("No markdown content for file {FileId}, skip metadata analysis", documentFileId);
                return;
            }

            // Extract first 2000 chars of markdown (human-readable, not JSON)
            var textPreview = markdownContent.Length > 2000 ? markdownContent[..2000] : markdownContent;

            _logger.LogInformation("Starting LLM metadata analysis for file {FileId}", documentFileId);
            var analysis = await llmService.AnalyzeMetadataAsync(textPreview, ct);

            if (analysis == null)
            {
                _logger.LogWarning("LLM metadata analysis returned null for file {FileId}", documentFileId);
                return;
            }

            // Only fill in fields that are currently empty (don't overwrite existing values)
            var newSubject = string.IsNullOrWhiteSpace(file.Subject) ? analysis.Subject : file.Subject;
            var newGrade = string.IsNullOrWhiteSpace(file.Grade) ? analysis.Grade : file.Grade;
            var newYear = string.IsNullOrWhiteSpace(file.Year) ? analysis.Year : file.Year;

            // Check if anything actually changed
            if (newSubject == file.Subject && newGrade == file.Grade && newYear == file.Year)
            {
                _logger.LogInformation("LLM did not provide any missing metadata for file {FileId}", documentFileId);
                return;
            }

            await fileService.UpdateMetadataAsync(documentFileId, newSubject, newGrade, newYear);
            _logger.LogInformation(
                "Metadata updated by LLM for file {FileId}: Subject={Subject}, Grade={Grade}, Year={Year}",
                documentFileId, newSubject ?? "(none)", newGrade ?? "(none)", newYear ?? "(none)");

            // Sync OpenSearch index with new metadata (best-effort)
            try
            {
                var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
                await searchIndexService.UpdateDocumentFileMetadataAsync(documentFileId, newSubject, newGrade, newYear);
            }
            catch (Exception syncEx)
            {
                _logger.LogWarning(syncEx, "Failed to sync OpenSearch metadata for file {FileId}", documentFileId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM metadata analysis failed for file {FileId}", documentFileId);
        }
    }
}
