using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public class MinerUFileParseWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MinerUFileParseWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _mineruPollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _mineruTimeout = TimeSpan.FromMinutes(30);
    private const int MaxPagesPerChunk = 200;

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
                var fileService = scope.ServiceProvider.GetRequiredService<IDocumentFileService>();
                var parseService = scope.ServiceProvider.GetRequiredService<IDocumentParseService>();
                var minerUClient = scope.ServiceProvider.GetRequiredService<MinerUPrecisionClient>();
                var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();

                var pendingJobs = await parseService.GetPendingJobsAsync();

                var pdfSplitService = scope.ServiceProvider.GetRequiredService<IPdfSplitService>();
                var fileConversionService = scope.ServiceProvider.GetRequiredService<IFileConversionService>();

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await ProcessFileAsync(job, fileService, parseService, minerUClient, ossService, pdfSplitService, fileConversionService, scope.ServiceProvider, stoppingToken);
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

    private async Task ProcessFileAsync(
        DocumentParseModel parse,
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        MinerUPrecisionClient minerUClient,
        IOssService ossService,
        IPdfSplitService pdfSplitService,
        IFileConversionService fileConversionService,
        IServiceProvider scopeProvider,
        CancellationToken ct)
    {
        // Step 1: Update status to parsing
        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing);

        // Step 2: Get file info
        var file = await fileService.GetByIdAsync(parse.DocumentFileId)
            ?? throw new InvalidOperationException($"Document file not found: {parse.DocumentFileId}");

        // Step 3: Download source file
        var sourceStream = await ossService.DownloadAsync(file.FilePath);

        // Step 4: Convert non-PDF files to PDF first
        Stream pdfStream;
        bool isConverted = false;

        if (!file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
            && !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Non-PDF file detected: {FileName} ({ContentType}), converting to PDF", file.FileName, file.ContentType);

            if (!fileConversionService.IsAvailable)
            {
                sourceStream.Dispose();
                await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed,
                    errorMessage: "LibreOffice is not installed. Cannot convert non-PDF files to PDF for parsing. Please install LibreOffice or upload PDF files only.");
                return;
            }

            var convertedStream = await fileConversionService.ConvertToPdfAsync(sourceStream, file.FileName, ct);
            sourceStream.Dispose();

            if (convertedStream == null)
            {
                await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed,
                    errorMessage: $"Failed to convert {file.FileName} to PDF. The file may be corrupted or in an unsupported format.");
                return;
            }

            pdfStream = convertedStream;
            isConverted = true;
            _logger.LogInformation("Successfully converted {FileName} to PDF", file.FileName);
        }
        else
        {
            pdfStream = sourceStream;
        }

        // Block service from scope (scoped lifetime)
        var blockService = scopeProvider.GetRequiredService<IDocumentParseBlockService>();

        try
        {
            // Step 5: Check PDF page count
            var pageCount = pdfSplitService.GetPageCount(pdfStream);
            _logger.LogInformation("File {FileId} has {PageCount} pages (converted: {IsConverted})", file.Id, pageCount, isConverted);

            if (pageCount <= MaxPagesPerChunk)
            {
                // Small file: process directly
                await ProcessSingleFileAsync(parse, file, ossService, minerUClient, parseService, blockService, scopeProvider, ct);
            }
            else
            {
                // Large file: split, parse each chunk, merge results
                await ProcessSplitFileAsync(parse, file, pdfStream, pageCount, pdfSplitService, ossService, minerUClient, parseService, blockService, scopeProvider, ct);
            }
        }
        finally
        {
            pdfStream.Dispose();
        }
    }

    /// <summary>
    /// Process a file that fits within MinerU's page limit (original flow).
    /// </summary>
    private async Task ProcessSingleFileAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        IOssService ossService,
        MinerUPrecisionClient minerUClient,
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        IServiceProvider scopeProvider,
        CancellationToken ct)
    {
        var presignedUrl = await ossService.GetPresignedUrlAsync(file.FilePath, 3600);
        _logger.LogInformation("Generated presigned URL for file {FileId}", file.Id);

        var dataId = parse.DocumentFileId.ToString("N")[..16];
        var taskId = await minerUClient.SubmitUrlAsync(presignedUrl, dataId, modelVersion: parse.ModelVersion, ct);
        _logger.LogInformation("MinerU task submitted: FileId={FileId}, TaskId={TaskId}", file.Id, taskId);

        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing, externalTaskId: taskId);

        var result = await PollAndDownloadAsync(taskId, minerUClient, ossService, ct);
        _logger.LogInformation("MinerU ZIP processed: FileId={FileId}, Images={ImageCount}", file.Id, result.Images.Count);

        await PersistParseResultAsync(parse, file, result, ossService, parseService, blockService, scopeProvider, ct);
    }

    /// <summary>
    /// Persist the full MinerU parse result: upload ZIP to OSS,
    /// insert images, parse and insert blocks, then mark the parse as Parsed.
    /// </summary>
    private async Task PersistParseResultAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        MinerUParseResult result,
        IOssService ossService,
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        IServiceProvider scopeProvider,
        CancellationToken ct)
    {
        // 1. Upload the full ZIP to OSS (raw data backup)
        string? zipPath = null;
        try
        {
            zipPath = await ossService.UploadAsync(
                result.ZipBytes,
                "mineru-output.zip",
                "application/zip",
                OssBucket.Documents,
                $"mineru/{parse.DocumentFileId}");
            _logger.LogInformation("Full ZIP uploaded: {Path} ({Size} bytes)", zipPath, result.ZipBytes.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload full ZIP for parse {ParseId}", parse.Id);
        }

        // 2. Insert images and build the name -> id map for block referencing
        var imageNameToId = new Dictionary<string, Guid>();
        foreach (var img in result.Images)
        {
            var imageModel = new DocumentParseImageModel
            {
                ParseId = parse.Id,
                ImageName = img.ImageName,
                ImagePath = img.S3Path,
                ContentType = img.ContentType,
            };
            await parseService.AddImageAsync(imageModel);
            imageNameToId[img.ImageName] = imageModel.Id;
        }
        _logger.LogInformation("Inserted {ImageCount} images for parse {ParseId}", result.Images.Count, parse.Id);

        // 3. Parse content_list.json and insert blocks
        if (!string.IsNullOrWhiteSpace(result.ContentListJson) && result.ContentListJson != "[]")
        {
            await blockService.InsertBlocksFromContentListAsync(parse.Id, result.ContentListJson, imageNameToId);
        }

        // 4. Update parse with markdown, content_list, and new JSON fields
        await parseService.UpdateStatusAsync(
            parse.Id,
            DocumentParseStatus.Parsed,
            markdownContent: result.Markdown,
            contentList: result.ContentListJson,
            contentListV2: result.ContentListV2Json,
            modelJson: result.ModelJson,
            layoutJson: result.LayoutJson,
            zipPath: zipPath);
        _logger.LogInformation("MinerU parse completed: ParseId={ParseId}, FileId={FileId}, " +
            "contentListV2={HasV2}, modelJson={HasModel}, layoutJson={HasLayout}",
            parse.Id, file.Id,
            result.ContentListV2Json != null ? "yes" : "no",
            result.ModelJson != null ? "yes" : "no",
            result.LayoutJson != null ? "yes" : "no");

        // 5. Index blocks to OpenSearch (best-effort, failure does not block parse)
        await IndexBlocksToSearchAsync(parse.Id, file.Id, file.FileName, scopeProvider);
    }

    /// <summary>
    /// Index parse blocks to OpenSearch. Best-effort: failures are logged but do not block the parse workflow.
    /// </summary>
    private async Task IndexBlocksToSearchAsync(Guid parseId, Guid documentFileId, string fileName, IServiceProvider scopeProvider)
    {
        try
        {
            var searchIndexService = scopeProvider.GetRequiredService<ISearchIndexService>();
            await searchIndexService.IndexParseBlocksAsync(parseId, documentFileId, fileName, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to index parse {ParseId} to OpenSearch", parseId);
        }
    }

    /// <summary>
    /// Process a large file by splitting it into chunks, parsing each, and merging results.
    /// </summary>
    private async Task ProcessSplitFileAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        Stream sourceStream,
        int pageCount,
        IPdfSplitService pdfSplitService,
        IOssService ossService,
        MinerUPrecisionClient minerUClient,
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        IServiceProvider scopeProvider,
        CancellationToken ct)
    {
        _logger.LogInformation("Large file detected: {PageCount} pages, splitting into chunks of {MaxPages}", pageCount, MaxPagesPerChunk);

        // Step 1: Split PDF into chunks
        sourceStream.Position = 0;
        var chunks = pdfSplitService.SplitPdf(sourceStream, MaxPagesPerChunk);
        var chunkS3Paths = new List<string>();
        var chunkResults = new List<MinerUParseResult>();

        try
        {
            // Step 2: Upload each chunk to S3
            for (int i = 0; i < chunks.Count; i++)
            {
                var (chunkIndex, chunkStream) = chunks[i];
                var chunkPath = $"mineru/splits/{parse.Id}/chunk_{chunkIndex}.pdf";

                chunkStream.Position = 0;
                using var ms = new MemoryStream();
                await chunkStream.CopyToAsync(ms, ct);
                var chunkBytes = ms.ToArray();

                var s3Path = await ossService.UploadAsync(chunkBytes, $"chunk_{chunkIndex}.pdf", "application/pdf", OssBucket.Documents, $"mineru/splits/{parse.Id}");
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
                    var chunkPresignedUrl = await ossService.GetPresignedUrlAsync(chunkS3Paths[i], 3600);
                    var dataId = $"{parse.DocumentFileId:N}"[..16] + $"_chunk{i}";
                    var taskId = await minerUClient.SubmitUrlAsync(chunkPresignedUrl, dataId, modelVersion: parse.ModelVersion, ct);
                    _logger.LogInformation("Chunk {Index} submitted: TaskId={TaskId}", i, taskId);

                    var chunkResult = await PollAndDownloadAsync(taskId, minerUClient, ossService, ct);
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
                    await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed, errorMessage: errorMsg);
                    return;
                }

                // Some chunks succeeded — save partial results but mark as failed
                await PersistMergedChunkResultsAsync(parse, file, chunkResults, ossService, parseService, blockService, scopeProvider, errorMsg, status: DocumentParseStatus.Failed);
                return;
            }

            // Step 5: All chunks succeeded — merge and persist
            await PersistMergedChunkResultsAsync(parse, file, chunkResults, ossService, parseService, blockService, scopeProvider, errorMsg: null, status: DocumentParseStatus.Parsed);
            _logger.LogInformation("Split parse completed: ParseId={ParseId}, FileId={FileId}, Chunks={ChunkCount}",
                parse.Id, file.Id, chunks.Count);
        }
        finally
        {
            // Step 6: Clean up temporary chunk files from S3
            foreach (var s3Path in chunkS3Paths)
            {
                try
                {
                    await ossService.DeleteAsync(s3Path);
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
    /// Persist merged results from multiple chunks: combine markdown/content_list/blocks across chunks,
    /// prefix image names with chunk index to avoid collisions, and persist all artifacts.
    /// </summary>
    private async Task PersistMergedChunkResultsAsync(
        DocumentParseModel parse,
        DocumentFileModel file,
        List<MinerUParseResult> chunkResults,
        IOssService ossService,
        IDocumentParseService parseService,
        IDocumentParseBlockService blockService,
        IServiceProvider scopeProvider,
        string? errorMsg,
        string status)
    {
        // Concatenate markdown with separator
        var allMarkdown = string.Join("\n\n---\n\n", chunkResults.Select(r => r.Markdown));

        // Concatenate content_list JSON arrays
        var mergedContentList = MergeContentListArrays(chunkResults);

        // Prefix image names with chunk index to avoid collisions across chunks
        var prefixedImages = new List<ImageMetadata>();
        for (int i = 0; i < chunkResults.Count; i++)
        {
            foreach (var img in chunkResults[i].Images)
            {
                var prefixedName = $"chunk{i}_{img.ImageName}";
                prefixedImages.Add(img with { ImageName = prefixedName });
            }
        }

        // Upload first chunk's ZIP as the canonical "mineru-output.zip" (covers all pages)
        var firstZip = chunkResults[0].ZipBytes;
        string? zipPath = null;
        try
        {
            zipPath = await ossService.UploadAsync(
                firstZip,
                "mineru-output.zip",
                "application/zip",
                OssBucket.Documents,
                $"mineru/{parse.DocumentFileId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload ZIP for split parse {ParseId}", parse.Id);
        }

        // Use first chunk's layout.json and model.json if available
        var firstLayoutJson = chunkResults.FirstOrDefault(r => r.LayoutJson != null)?.LayoutJson;
        var firstModelJson = chunkResults.FirstOrDefault(r => r.ModelJson != null)?.ModelJson;
        var firstContentListV2 = chunkResults.FirstOrDefault(r => r.ContentListV2Json != null)?.ContentListV2Json;

        // Insert images (with prefixed names so they map to prefixed blocks)
        var imageNameToId = new Dictionary<string, Guid>();
        foreach (var img in prefixedImages)
        {
            var imageModel = new DocumentParseImageModel
            {
                ParseId = parse.Id,
                ImageName = img.ImageName,
                ImagePath = img.S3Path,
                ContentType = img.ContentType,
            };
            await parseService.AddImageAsync(imageModel);
            imageNameToId[img.ImageName] = imageModel.Id;
        }

        // Insert blocks (block service handles overwriting)
        if (!string.IsNullOrEmpty(mergedContentList) && mergedContentList != "[]")
        {
            await blockService.InsertBlocksFromContentListAsync(parse.Id, mergedContentList, imageNameToId);
        }

        await parseService.UpdateStatusAsync(
            parse.Id,
            status,
            errorMessage: errorMsg,
            markdownContent: allMarkdown,
            contentList: mergedContentList,
            contentListV2: firstContentListV2,
            modelJson: firstModelJson,
            layoutJson: firstLayoutJson,
            zipPath: zipPath);

        // Index blocks to OpenSearch only when parse succeeded (best-effort)
        if (status == DocumentParseStatus.Parsed)
        {
            await IndexBlocksToSearchAsync(parse.Id, file.Id, file.FileName, scopeProvider);
        }
    }

    /// <summary>
    /// Merge multiple content_list.json arrays (one per chunk) into a single JSON array.
    /// Block page_id is preserved; sort_index is recomputed in DocumentParseBlockService.
    /// </summary>
    private static string MergeContentListArrays(List<MinerUParseResult> chunkResults)
    {
        var allBlocks = new List<JsonElement>();
        foreach (var chunk in chunkResults)
        {
            if (string.IsNullOrWhiteSpace(chunk.ContentListJson) || chunk.ContentListJson == "[]") continue;
            try
            {
                using var doc = JsonDocument.Parse(chunk.ContentListJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        allBlocks.Add(el.Clone());
                    }
                }
            }
            catch
            {
                // Skip malformed chunk's content_list
            }
        }
        return JsonSerializer.Serialize(allBlocks);
    }

    /// <summary>
    /// Poll MinerU task status until done, then download and process the ZIP.
    /// </summary>
    private async Task<MinerUParseResult> PollAndDownloadAsync(
        string taskId,
        MinerUPrecisionClient minerUClient,
        IOssService ossService,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _mineruTimeout;
        string? fullZipUrl = null;

        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var (state, zipUrl, errMsg) = await minerUClient.PollStatusAsync(taskId, ct);

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

        return await minerUClient.DownloadAndProcessZipAsync(fullZipUrl, taskId, ossService, ct);
    }
}
