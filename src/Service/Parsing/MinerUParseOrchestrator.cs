using Microsoft.Extensions.Logging;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service.Parsing;

/// <summary>
/// Orchestrates the MinerU parse workflow for a single pending parse job:
/// file download, optional PDF conversion, page count check, single-file or split-file processing.
/// </summary>
public sealed class MinerUParseOrchestrator
{
    private readonly IDocumentFileService _fileService;
    private readonly IDocumentParseService _parseService;
    private readonly MinerUPrecisionClient _minerUClient;
    private readonly IOssService _ossService;
    private readonly IPdfSplitService _pdfSplitService;
    private readonly IFileConversionService _fileConversionService;
    private readonly MinerUResultPersistence _persistence;
    private readonly ILogger<MinerUParseOrchestrator> _logger;

    private readonly TimeSpan _mineruPollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _mineruTimeout = TimeSpan.FromMinutes(30);
    private const int MaxPagesPerChunk = 200;

    public MinerUParseOrchestrator(
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        MinerUPrecisionClient minerUClient,
        IOssService ossService,
        IPdfSplitService pdfSplitService,
        IFileConversionService fileConversionService,
        MinerUResultPersistence persistence,
        IServiceProvider scopeProvider,
        ILogger<MinerUParseOrchestrator> logger)
    {
        _fileService = fileService;
        _parseService = parseService;
        _minerUClient = minerUClient;
        _ossService = ossService;
        _pdfSplitService = pdfSplitService;
        _fileConversionService = fileConversionService;
        _persistence = persistence;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single pending parse job end-to-end. Returns the final markdown on success,
    /// or null when the failure has already been recorded in the parse status.
    /// </summary>
    internal async Task<string?> ProcessFileAsync(DocumentParseModel parse, CancellationToken ct)
    {
        // Step 1: Update status to parsing
        await _parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing);

        // Step 2: Get file info
        var file = await _fileService.GetByIdAsync(parse.DocumentFileId)
            ?? throw new InvalidOperationException($"Document file not found: {parse.DocumentFileId}");

        // Step 3: Download source file
        var sourceStream = await _ossService.DownloadAsync(file.FilePath);

        // Step 4: Convert non-PDF files to PDF first
        Stream pdfStream;
        bool isConverted = false;
        string? convertedPdfOssPath = null;

        if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Non-PDF file detected: {FileName} ({ContentType}), converting to PDF", file.FileName, file.ContentType);

            Stream? convertedStream = null;
            try
            {
                convertedStream = await _fileConversionService.ConvertToPdfAsync(sourceStream, file.FileName, ct);
            }
            finally
            {
                sourceStream.Dispose();
            }

            if (convertedStream == null)
            {
                await _parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed,
                    errorMessage: $"Failed to convert {file.FileName} to PDF via doc-converter. The file may be corrupted, in an unsupported format, or doc-converter service is unavailable.");
                return null;
            }

            pdfStream = convertedStream;
            isConverted = true;
            _logger.LogInformation("Successfully converted {FileName} to PDF", file.FileName);
        }
        else
        {
            pdfStream = sourceStream;
        }

        // If the file was converted to PDF, upload the converted PDF to OSS so that
        // MinerU receives a PDF URL (not the original DOCX). This ensures behavior
        // consistency between small-file and large-file (split) paths.
        string mineruOssPath = file.FilePath;
        if (isConverted)
        {
            pdfStream.Position = 0;
            using var ms = new MemoryStream();
            await pdfStream.CopyToAsync(ms, ct);
            var pdfBytes = ms.ToArray();

            convertedPdfOssPath = await _ossService.UploadAsync(
                pdfBytes,
                $"{file.FileName}.pdf",
                "application/pdf",
                OssBucket.Documents,
                $"mineru/converted/{parse.Id}");
            mineruOssPath = convertedPdfOssPath;
            _logger.LogInformation("Converted PDF uploaded to OSS: {Path}", convertedPdfOssPath);
        }

        try
        {
            // Step 5: Check PDF page count
            var pageCount = _pdfSplitService.GetPageCount(pdfStream);
            _logger.LogInformation("File {FileId} has {PageCount} pages (converted: {IsConverted})", file.Id, pageCount, isConverted);

            if (pageCount <= MaxPagesPerChunk)
            {
                // Small file: process directly
                return await ProcessSingleFileAsync(parse, file, mineruOssPath, ct);
            }
            else
            {
                // Large file: split, parse each chunk, merge results
                return await ProcessSplitFileAsync(parse, file, pdfStream, pageCount, ct);
            }
        }
        finally
        {
            pdfStream.Dispose();

            // Clean up temporary converted PDF from OSS
            if (convertedPdfOssPath != null)
            {
                try
                {
                    await _ossService.DeleteAsync(convertedPdfOssPath);
                    _logger.LogInformation("Cleaned up temporary converted PDF: {Path}", convertedPdfOssPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean up temporary converted PDF: {Path}", convertedPdfOssPath);
                }
            }
        }
    }

    /// <summary>
    /// Process a file that fits within MinerU's page limit (original flow).
    /// mineruOssPath is the OSS path of the PDF to submit to MinerU (original PDF or converted PDF).
    /// </summary>
    private async Task<string> ProcessSingleFileAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        string mineruOssPath,
        CancellationToken ct)
    {
        var presignedUrl = await _ossService.GetPresignedUrlAsync(mineruOssPath, 3600);
        _logger.LogInformation("Generated presigned URL for file {FileId} from {OssPath}", file.Id, mineruOssPath);

        var dataId = parse.DocumentFileId.ToString("N")[..16];
        var taskId = await _minerUClient.SubmitUrlAsync(presignedUrl, dataId, modelVersion: parse.ModelVersion, ct);
        _logger.LogInformation("MinerU task submitted: FileId={FileId}, TaskId={TaskId}", file.Id, taskId);

        await _parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing, externalTaskId: taskId);

        var result = await PollAndDownloadAsync(taskId, ct);
        _logger.LogInformation("MinerU ZIP processed: FileId={FileId}, Images={ImageCount}", file.Id, result.Images.Count);

        await _persistence.PersistParseResultAsync(parse, file, result, ct);
        return result.Markdown;
    }

    /// <summary>
    /// Process a large file by splitting it into chunks, parsing each, and merging results.
    /// </summary>
    private async Task<string?> ProcessSplitFileAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        Stream sourceStream,
        int pageCount,
        CancellationToken ct)
    {
        _logger.LogInformation("Large file detected: {PageCount} pages, splitting into chunks of {MaxPages}", pageCount, MaxPagesPerChunk);

        // Step 1: Split PDF into chunks
        sourceStream.Position = 0;
        var chunks = _pdfSplitService.SplitPdf(sourceStream, MaxPagesPerChunk);
        var chunkS3Paths = new List<string>();
        var chunkResults = new List<MinerUParseResult>();

        try
        {
            // Step 2: Upload each chunk to S3
            for (int i = 0; i < chunks.Count; i++)
            {
                var (chunkIndex, chunkStream) = chunks[i];

                chunkStream.Position = 0;
                using var ms = new MemoryStream();
                await chunkStream.CopyToAsync(ms, ct);
                var chunkBytes = ms.ToArray();

                var s3Path = await _ossService.UploadAsync(chunkBytes, $"chunk_{chunkIndex}.pdf", "application/pdf", OssBucket.Documents, $"mineru/splits/{parse.Id}");
                chunkS3Paths.Add(s3Path);
                _logger.LogInformation("Chunk {Index} uploaded to S3: {Path}", chunkIndex, s3Path);
            }

            // Step 3: Submit each chunk to MinerU and collect results
            var errors = new List<string>();

            for (int i = 0; i < chunkS3Paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var chunkPresignedUrl = await _ossService.GetPresignedUrlAsync(chunkS3Paths[i], 3600);
                    var dataId = $"{parse.DocumentFileId:N}"[..16] + $"_chunk{i}";
                    var taskId = await _minerUClient.SubmitUrlAsync(chunkPresignedUrl, dataId, modelVersion: parse.ModelVersion, ct);
                    _logger.LogInformation("Chunk {Index} submitted: TaskId={TaskId}", i, taskId);

                    var chunkResult = await PollAndDownloadAsync(taskId, ct);
                    chunkResults.Add(chunkResult);
                    _logger.LogInformation("Chunk {Index} processed: {ImageCount} images", i, chunkResult.Images.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Chunk {Index} failed", i);
                    errors.Add($"Chunk {i} (pages {i * MaxPagesPerChunk + 1}-{Math.Min((i + 1) * MaxPagesPerChunk, pageCount)}): {ex.Message}");
                }
            }

            // Step 4: Check for failures
            if (errors.Count > 0)
            {
                var errorMsg = $"Split parse partially failed ({errors.Count}/{chunks.Count} chunks): {string.Join("; ", errors)}";

                if (chunkResults.Count == 0)
                {
                    // All chunks failed
                    await _parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed, errorMessage: errorMsg);
                    return null;
                }

                // Some chunks succeeded — save partial results but mark as failed
                await _persistence.PersistMergedChunkResultsAsync(parse, file, chunkResults, errorMsg, status: DocumentParseStatus.Failed, ct);
                return null;
            }

            // Step 5: All chunks succeeded — merge and persist
            await _persistence.PersistMergedChunkResultsAsync(parse, file, chunkResults, errorMsg: null, status: DocumentParseStatus.Parsed, ct);
            _logger.LogInformation("Split parse completed: ParseId={ParseId}, FileId={FileId}, Chunks={ChunkCount}",
                parse.Id, file.Id, chunks.Count);

            return string.Join("\n\n---\n\n", chunkResults.Select(r => r.Markdown));
        }
        finally
        {
            // Step 6: Clean up temporary chunk files from S3
            foreach (var s3Path in chunkS3Paths)
            {
                try
                {
                    await _ossService.DeleteAsync(s3Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete temporary chunk from S3: {Path}", s3Path);
                }
            }

            // Dispose chunk streams
            foreach (var (_, chunkStream) in chunks)
            {
                chunkStream.Dispose();
            }
        }
    }

    /// <summary>
    /// Poll MinerU task status until done, then download and process the ZIP.
    /// </summary>
    private async Task<MinerUParseResult> PollAndDownloadAsync(
        string taskId,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _mineruTimeout;
        string? fullZipUrl = null;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var (state, zipUrl, errMsg) = await _minerUClient.PollStatusAsync(taskId, ct);

            if (state == "done")
            {
                fullZipUrl = zipUrl;
                break;
            }

            if (state == "failed")
            {
                throw new InvalidOperationException($"MinerU parse failed: {errMsg}");
            }

            await Task.Delay(_mineruPollInterval, ct);
        }

        if (fullZipUrl == null)
        {
            throw new InvalidOperationException("MinerU parse timed out");
        }

        return await _minerUClient.DownloadAndProcessZipAsync(fullZipUrl, taskId, _ossService, ct);
    }
}
