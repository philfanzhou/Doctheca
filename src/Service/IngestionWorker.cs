using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.DocRetrieval.Domain.Repositories;
using Ruoyu.Study.DocRetrieval.Domain.Services;

namespace Ruoyu.Study.DocRetrieval.Service;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly ISearchIndexService? _searchIndexService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    public IngestionWorker(IServiceProvider serviceProvider, ILogger<IngestionWorker> logger, ISearchIndexService? searchIndexService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _searchIndexService = searchIndexService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("文档导入后台工作器已启动");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var domainService = scope.ServiceProvider.GetRequiredService<DocumentDomainService>();

                var pendingJobs = await domainService.GetPendingJobsAsync();

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await domainService.StartIngestionJobAsync(job.Id, "v1.0", null);
                        _logger.LogInformation("开始处理导入任务：{JobId}，文档：{DocumentId}", job.Id, job.DocumentId);

                        // TODO: 实际的文档解析逻辑将在文档解析服务实现后接入
                        // 当前标记为成功，使流程可走通
                        await domainService.CompleteIngestionJobAsync(job.Id);
                        _logger.LogInformation("导入任务完成：{JobId}", job.Id);

                        // 导入完成后，将文档segments写入搜索索引
                        if (_searchIndexService != null)
                        {
                            try
                            {
                                var document = await domainService.GetDocumentAsync(job.DocumentId);
                                if (document != null)
                                {
                                    await _searchIndexService.IndexDocumentSegmentsAsync(
                                        document.Id, document.Title, document.Subject, document.Grade, document.Year);
                                    _logger.LogInformation("文档搜索索引已创建：{DocumentId}", job.DocumentId);
                                }
                            }
                            catch (Exception indexEx)
                            {
                                _logger.LogError(indexEx, "创建文档搜索索引失败：{DocumentId}", job.DocumentId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "导入任务失败：{JobId}", job.Id);
                        try
                        {
                            await domainService.FailIngestionJobAsync(job.Id, ex.Message);
                        }
                        catch (Exception failEx)
                        {
                            _logger.LogError(failEx, "标记导入任务失败时出错：{JobId}", job.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入工作器轮询出错");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
