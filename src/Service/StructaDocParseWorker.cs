using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service.Parsing;
using Ruoyu.Study.DocLibrary.Service.StructaDoc;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Background worker driving the StructaDoc parse pipeline (ADR-0009).
/// Pending jobs are submitted to StructaDoc as Parse Runs; parsing jobs are polled
/// until terminal, then results are synced locally and indexed. Transient StructaDoc
/// failures keep the job in its current status for the next polling cycle.
/// </summary>
public sealed class StructaDocParseWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<StructaDocParseWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);

    public StructaDocParseWorker(IServiceProvider serviceProvider, ILogger<StructaDocParseWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StructaDoc parse worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var parseService = scope.ServiceProvider.GetRequiredService<IDocumentParseService>();

                var activeJobs = await parseService.GetActiveJobsAsync();

                foreach (var job in activeJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (job.Status == DocumentParseStatus.Pending)
                        {
                            await SubmitAsync(job, scope.ServiceProvider, stoppingToken);
                        }
                        else if (job.Status == DocumentParseStatus.Parsing)
                        {
                            await PollAsync(job, scope.ServiceProvider, stoppingToken);
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (StructaDocException ex) when (ex.IsTransient)
                    {
                        // Retry on the next polling cycle without failing the job.
                        _logger.LogWarning(
                            "Transient StructaDoc failure for parse {ParseId}, will retry: {Message}",
                            job.Id, ex.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "StructaDoc parse failed for parse {ParseId}", job.Id);
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
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StructaDoc parse worker polling error");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }

    /// <summary>
    /// Submit a pending parse to StructaDoc. Legacy files (stored in OSS before the migration)
    /// are lazily uploaded to StructaDoc first and attached to the file record.
    /// </summary>
    internal async Task SubmitAsync(DocumentParseModel job, IServiceProvider scopeProvider, CancellationToken ct)
    {
        var parseService = scopeProvider.GetRequiredService<IDocumentParseService>();
        var fileService = scopeProvider.GetRequiredService<IDocumentFileService>();
        var client = scopeProvider.GetRequiredService<IStructaDocClient>();
        var options = scopeProvider.GetRequiredService<IOptions<StructaDocOptions>>().Value;

        var file = await fileService.GetByIdAsync(job.DocumentFileId)
            ?? throw new InvalidOperationException($"Document file not found: {job.DocumentFileId}");

        var documentId = file.StructaDocDocumentId;
        if (documentId == null)
        {
            documentId = await AttachLegacyFileAsync(file, scopeProvider, ct);
        }

        var run = await client.CreateParseRunAsync(
            documentId.Value,
            idempotencyKey: job.Id.ToString("N"),
            providerConfigId: options.GetProviderConfigId(job.ModelVersion),
            ct: ct);

        await parseService.UpdateStatusAsync(
            job.Id,
            DocumentParseStatus.Parsing,
            externalTaskId: run.Id.ToString("D"),
            structaDocParseRunId: run.Id);

        _logger.LogInformation(
            "Submitted StructaDoc parse run {ParseRunId} for parse {ParseId} (document {DocumentId}, model {ModelVersion})",
            run.Id, job.Id, documentId.Value, job.ModelVersion);
    }

    private async Task<Guid> AttachLegacyFileAsync(DocumentFileModel file, IServiceProvider scopeProvider, CancellationToken ct)
    {
        var fileService = scopeProvider.GetRequiredService<IDocumentFileService>();
        var client = scopeProvider.GetRequiredService<IStructaDocClient>();
        var ossService = scopeProvider.GetRequiredService<IOssService>();

        if (string.IsNullOrEmpty(file.FilePath))
        {
            throw new InvalidOperationException(
                $"Document file {file.Id} has neither a StructaDoc reference nor an OSS source path.");
        }

        _logger.LogInformation(
            "Uploading legacy document file {FileId} to StructaDoc before parsing", file.Id);

        await using var source = await ossService.DownloadAsync(file.FilePath);
        var uploaded = await client.UploadDocumentAsync(
            file.FileName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            source, ct);

        var attached = await fileService.AttachStructaDocDocumentAsync(file.Id, uploaded.Id);
        if (attached == null)
        {
            throw new InvalidOperationException($"Document file disappeared during StructaDoc attach: {file.Id}");
        }

        return uploaded.Id;
    }

    /// <summary>Poll a submitted StructaDoc Parse Run and finish the local parse on terminal states.</summary>
    internal async Task PollAsync(DocumentParseModel job, IServiceProvider scopeProvider, CancellationToken ct)
    {
        var parseService = scopeProvider.GetRequiredService<IDocumentParseService>();
        var fileService = scopeProvider.GetRequiredService<IDocumentFileService>();
        var client = scopeProvider.GetRequiredService<IStructaDocClient>();
        var sync = scopeProvider.GetRequiredService<IStructaDocParseResultSync>();

        if (job.StructaDocParseRunId is not Guid runId)
        {
            // Legacy record left in "parsing" by the pre-migration MinerU worker; not recoverable.
            await parseService.UpdateStatusAsync(
                job.Id,
                DocumentParseStatus.Failed,
                "Parse was interrupted by the StructaDoc pipeline migration; please trigger parsing again.");
            return;
        }

        var run = await client.GetParseRunAsync(runId, ct);
        if (run == null)
        {
            await parseService.UpdateStatusAsync(
                job.Id,
                DocumentParseStatus.Failed,
                $"StructaDoc parse run {runId} no longer exists.");
            return;
        }

        switch (run.Status)
        {
            case StructaDocParseRunStatus.Succeeded:
            {
                var markdown = await sync.SyncAsync(job, runId, ct);

                var file = await fileService.GetByIdAsync(job.DocumentFileId)
                    ?? throw new InvalidOperationException($"Document file not found: {job.DocumentFileId}");

                await IndexBlocksToSearchAsync(job.Id, file, scopeProvider);
                await AnalyzeMetadataIfMissingAsync(job.DocumentFileId, markdown, scopeProvider, ct);
                break;
            }

            case StructaDocParseRunStatus.Failed:
            case StructaDocParseRunStatus.Cancelled:
            {
                var reason = string.IsNullOrWhiteSpace(run.ErrorCode)
                    ? run.ErrorMessage
                    : $"{run.ErrorCode}: {run.ErrorMessage}";
                await parseService.UpdateStatusAsync(
                    job.Id,
                    DocumentParseStatus.Failed,
                    $"StructaDoc parse run {run.Status}" + (reason != null ? $" ({reason})" : string.Empty));
                break;
            }

            default:
                _logger.LogDebug(
                    "StructaDoc parse run {ParseRunId} for parse {ParseId} is {Status} (stage {Stage}, attempt {Attempt}/{Max})",
                    runId, job.Id, run.Status, run.Stage, run.AttemptCount, run.MaxAttempts);
                break;
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
