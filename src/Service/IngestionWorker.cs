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
    private readonly IQdrantService? _qdrantService;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

    public IngestionWorker(IServiceProvider serviceProvider, ILogger<IngestionWorker> logger, ISearchIndexService? searchIndexService = null, IQdrantService? qdrantService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _searchIndexService = searchIndexService;
        _qdrantService = qdrantService;
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
                var parserService = scope.ServiceProvider.GetRequiredService<IDocumentParserService>();
                var ossService = scope.ServiceProvider.GetRequiredService<IOssService>();
                var pageRepository = scope.ServiceProvider.GetRequiredService<IDocumentPageRepository>();
                var segmentRepository = scope.ServiceProvider.GetRequiredService<IDocumentSegmentRepository>();
                var questionRepository = scope.ServiceProvider.GetRequiredService<IQuestionSegmentRepository>();

                var pendingJobs = await domainService.GetPendingJobsAsync();

                foreach (var job in pendingJobs)
                {
                    stoppingToken.ThrowIfCancellationRequested();

                    try
                    {
                        await domainService.StartIngestionJobAsync(job.Id, "v1.0", null);
                        _logger.LogInformation("开始处理导入任务：{JobId}，文档：{DocumentId}", job.Id, job.DocumentId);

                        // 获取文档信息
                        var document = await domainService.GetDocumentAsync(job.DocumentId)
                            ?? throw new InvalidOperationException($"文档不存在：{job.DocumentId}");

                        // 从 OSS 下载文件流
                        using var fileStream = await ossService.DownloadAsync(document.FilePath);

                        // 调用文档解析服务
                        var parsedDocument = await parserService.ParseAsync(fileStream, document.SourceType, stoppingToken);
                        _logger.LogInformation("文档解析完成：{DocumentId}，共 {PageCount} 页", job.DocumentId, parsedDocument.Pages.Count);

                        // 将解析结果写入数据库
                        var pageModels = parsedDocument.Pages.Select(p => new DocumentPageModel
                        {
                            Id = Guid.NewGuid(),
                            DocumentId = document.Id,
                            PageNumber = p.PageNumber,
                            CreatedAt = DateTimeOffset.UtcNow
                        }).ToList();

                        await pageRepository.AddRangeAsync(pageModels);

                        // 建立 PageNumber -> PageId 映射
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

                        // 标记导入任务完成
                        await domainService.CompleteIngestionJobAsync(job.Id);
                        _logger.LogInformation("导入任务完成：{JobId}，共 {SegmentCount} 个片段，{QuestionCount} 道题目",
                            job.Id, segmentModels.Count, questionModels.Count);

                        // 导入完成后，将文档segments写入搜索索引
                        if (_searchIndexService != null)
                        {
                            try
                            {
                                await _searchIndexService.IndexDocumentSegmentsAsync(
                                    document.Id, document.Title, document.Subject, document.Grade, document.Year);
                                _logger.LogInformation("文档搜索索引已创建：{DocumentId}", job.DocumentId);
                            }
                            catch (Exception indexEx)
                            {
                                _logger.LogError(indexEx, "创建文档搜索索引失败：{DocumentId}", job.DocumentId);
                            }
                        }

                        // 导入完成后，将文档向量写入 Qdrant
                        if (_qdrantService != null)
                        {
                            try
                            {
                                await _qdrantService.IndexDocumentVectorsAsync(
                                    document.Id, document.Title, document.Subject, document.Grade, document.Year);
                                _logger.LogInformation("文档向量索引已创建：{DocumentId}", job.DocumentId);
                            }
                            catch (Exception vectorEx)
                            {
                                _logger.LogError(vectorEx, "创建文档向量索引失败：{DocumentId}", job.DocumentId);
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
