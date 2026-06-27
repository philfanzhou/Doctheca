using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
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
                        await ProcessFileAsync(job, fileService, parseService, minerUClient, ossService, pdfSplitService, fileConversionService, stoppingToken);
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

        try
        {
            // Step 5: Check PDF page count
            var pageCount = pdfSplitService.GetPageCount(pdfStream);
            _logger.LogInformation("File {FileId} has {PageCount} pages (converted: {IsConverted})", file.Id, pageCount, isConverted);

            if (pageCount <= MaxPagesPerChunk)
            {
                // Small file: process directly
                await ProcessSingleFileAsync(parse, file, ossService, minerUClient, parseService, ct);
            }
            else
            {
                // Large file: split, parse each chunk, merge results
                await ProcessSplitFileAsync(parse, file, pdfStream, pageCount, pdfSplitService, ossService, minerUClient, parseService, ct);
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
        CancellationToken ct)
    {
        var presignedUrl = await ossService.GetPresignedUrlAsync(file.FilePath, 3600);
        _logger.LogInformation("Generated presigned URL for file {FileId}", file.Id);

        var dataId = parse.DocumentFileId.ToString("N")[..16];
        var taskId = await minerUClient.SubmitUrlAsync(presignedUrl, dataId, ct);
        _logger.LogInformation("MinerU task submitted: FileId={FileId}, TaskId={TaskId}", file.Id, taskId);

        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing, externalTaskId: taskId);

        var (markdown, imageMetadataList) = await PollAndDownloadAsync(taskId, minerUClient, ossService, ct);
        _logger.LogInformation("MinerU ZIP processed: FileId={FileId}, Images={ImageCount}", file.Id, imageMetadataList.Count);

        foreach (var img in imageMetadataList)
        {
            await parseService.AddImageAsync(new DocumentParseImageModel
            {
                ParseId = parse.Id,
                ImageName = img.ImageName,
                ImagePath = img.S3Path,
                ContentType = img.ContentType,
            });
        }

        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsed, markdownContent: markdown);
        _logger.LogInformation("MinerU parse completed: ParseId={ParseId}, FileId={FileId}", parse.Id, file.Id);
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
        CancellationToken ct)
    {
        _logger.LogInformation("Large file detected: {PageCount} pages, splitting into chunks of {MaxPages}", pageCount, MaxPagesPerChunk);

        // Step 1: Split PDF into chunks
        sourceStream.Position = 0;
        var chunks = pdfSplitService.SplitPdf(sourceStream, MaxPagesPerChunk);
        var chunkS3Paths = new List<string>();

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
            var allMarkdown = new List<string>();
            var allImages = new List<ImageMetadata>();
            var errors = new List<string>();

            for (int i = 0; i < chunkS3Paths.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var chunkPresignedUrl = await ossService.GetPresignedUrlAsync(chunkS3Paths[i], 3600);
                    var dataId = $"{parse.DocumentFileId:N}"[..16] + $"_chunk{i}";
                    var taskId = await minerUClient.SubmitUrlAsync(chunkPresignedUrl, dataId, ct);
                    _logger.LogInformation("Chunk {Index} submitted: TaskId={TaskId}", i, taskId);

                    var (markdown, images) = await PollAndDownloadAsync(taskId, minerUClient, ossService, ct);

                    // Prefix image names with chunk index to avoid collisions
                    var prefixedMarkdown = markdown;
                    foreach (var img in images)
                    {
                        var prefixedImageName = $"chunk{i}_{img.ImageName}";
                        prefixedMarkdown = prefixedMarkdown.Replace(img.ImageName, prefixedImageName);
                        allImages.Add(img with { ImageName = prefixedImageName });
                    }

                    allMarkdown.Add(prefixedMarkdown);
                    _logger.LogInformation("Chunk {Index} processed: {ImageCount} images", i, images.Count);
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

                // Save images from successful chunks
                foreach (var img in allImages)
                {
                    await parseService.AddImageAsync(new DocumentParseImageModel
                    {
                        ParseId = parse.Id,
                        ImageName = img.ImageName,
                        ImagePath = img.S3Path,
                        ContentType = img.ContentType,
                    });
                }

                // If all chunks failed, mark as failed
                if (allMarkdown.Count == 0)
                {
                    await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed, errorMessage: errorMsg);
                    return;
                }

                // Some chunks succeeded — save partial results but mark as failed
                var mergedMarkdown = string.Join("\n\n---\n\n", allMarkdown);
                await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Failed,
                    errorMessage: errorMsg, markdownContent: mergedMarkdown);
                return;
            }

            // Step 5: Merge all results
            var finalMarkdown = string.Join("\n\n---\n\n", allMarkdown);

            foreach (var img in allImages)
            {
                await parseService.AddImageAsync(new DocumentParseImageModel
                {
                    ParseId = parse.Id,
                    ImageName = img.ImageName,
                    ImagePath = img.S3Path,
                    ContentType = img.ContentType,
                });
            }

            await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsed, markdownContent: finalMarkdown);
            _logger.LogInformation("Split parse completed: ParseId={ParseId}, FileId={FileId}, Chunks={ChunkCount}, Images={ImageCount}",
                parse.Id, file.Id, chunks.Count, allImages.Count);
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
    /// Poll MinerU task status until done, then download and process the ZIP.
    /// </summary>
    private async Task<(string Markdown, List<ImageMetadata> Images)> PollAndDownloadAsync(
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
