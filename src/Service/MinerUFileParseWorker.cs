using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocRetrieval.Domain.Models;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public class MinerUFileParseWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MinerUFileParseWorker> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _mineruPollInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _mineruTimeout = TimeSpan.FromMinutes(30);

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

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await ProcessFileAsync(job, fileService, parseService, minerUClient, ossService, stoppingToken);
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
        CancellationToken ct)
    {
        // Step 1: Update status to parsing
        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing);

        // Step 2: Get file info and generate presigned URL
        var file = await fileService.GetByIdAsync(parse.DocumentFileId)
            ?? throw new InvalidOperationException($"Document file not found: {parse.DocumentFileId}");
        var presignedUrl = await ossService.GetPresignedUrlAsync(file.FilePath, 3600);
        _logger.LogInformation("Generated presigned URL for file {FileId}", file.Id);

        // Step 3: Submit to MinerU API
        var dataId = parse.DocumentFileId.ToString("N")[..16];
        var taskId = await minerUClient.SubmitUrlAsync(presignedUrl, dataId, ct);
        _logger.LogInformation("MinerU task submitted: FileId={FileId}, TaskId={TaskId}", file.Id, taskId);

        // Step 4: Save external task ID
        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsing, externalTaskId: taskId);

        // Step 5: Poll MinerU status
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

        // Step 6: Download and process ZIP
        var (markdown, imageMetadataList) = await minerUClient.DownloadAndProcessZipAsync(fullZipUrl, taskId, ossService, ct);
        _logger.LogInformation("MinerU ZIP processed: FileId={FileId}, Images={ImageCount}", file.Id, imageMetadataList.Count);

        // Step 7: Save image metadata to document_parse_images table
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

        // Step 8: Save markdown content and update status
        await parseService.UpdateStatusAsync(parse.Id, DocumentParseStatus.Parsed, markdownContent: markdown);
        _logger.LogInformation("MinerU parse completed: ParseId={ParseId}, FileId={FileId}", parse.Id, file.Id);
    }
}
