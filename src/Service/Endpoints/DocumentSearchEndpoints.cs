using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;

namespace Doctheca.Service;

public static class DocumentSearchEndpoints
{
    public static WebApplication MapDocumentSearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/documents")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin);

        group.MapGet("/search", Search).RequireServiceMantleSecurityResponseHeaders();

        return app;
    }

    private static async Task<IResult> Search(
        ISearchDomainService searchService,
        ISearchPageLabelRepository pageLabels,
        CancellationToken cancellationToken,
        [FromServices] ILoggerFactory loggerFactory,
        [FromQuery] string query,
        [FromQuery] bool phrase = false,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? pageToken = null,
        [FromQuery] string? subject = null,
        [FromQuery] string? grade = null,
        [FromQuery] string? year = null,
        [FromQuery] string? documentTitle = null,
        // [Gen-2] minerU block-level filters (all optional; all null → zero regression V1 path)
        [FromQuery] string? blockType = null,
        [FromQuery] string? blockSubType = null,
        [FromQuery] int? pageNumber = null,
        [FromQuery] int? textLevel = null,
        [FromQuery] string? textFormat = null,
        [FromQuery] Guid? parseId = null,
        [FromQuery] Guid? documentFileId = null,
        [FromQuery] bool? hasImage = null,
        [FromQuery] Guid? structureNodeId = null)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentSearchEndpoints));

        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { success = false, message = "Query cannot be empty", errorCode = "DOCTHECA_QUERY_REQUIRED" });

        if (query.Length > 200)
            return Results.BadRequest(new { success = false, message = "Query exceeds 200 characters", errorCode = "DOCTHECA_QUERY_TOO_LONG" });

        pageSize = Math.Min(Math.Max(pageSize, 1), 100);

        // Build filter: V1 fields + [Gen-2] minerU fields (all null → filter stays null → zero regression)
        SearchFilterModel? filter = null;
        var hasV1Filter = !string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(grade)
            || !string.IsNullOrWhiteSpace(year) || !string.IsNullOrWhiteSpace(documentTitle);
        var hasMinerUFilter = !string.IsNullOrWhiteSpace(blockType) || !string.IsNullOrWhiteSpace(blockSubType)
            || pageNumber.HasValue || textLevel.HasValue || !string.IsNullOrWhiteSpace(textFormat)
            || parseId.HasValue || documentFileId.HasValue || hasImage.HasValue || structureNodeId.HasValue;

        if (hasV1Filter || hasMinerUFilter)
        {
            filter = new SearchFilterModel
            {
                Subject = subject,
                Grade = grade,
                Year = year,
                DocumentTitle = documentTitle,
                // [Gen-2] minerU filters
                BlockType = blockType,
                BlockSubType = blockSubType,
                PageNumber = pageNumber,
                TextLevel = textLevel,
                TextFormat = textFormat,
                ParseId = parseId,
                DocumentFileId = documentFileId,
                HasImage = hasImage,
                StructureNodeId = structureNodeId
            };
        }

        logger.LogInformation("Search request: phrase={Phrase}, pageSize={PageSize}, hasMinerUFilter={HasMinerUFilter}",
            phrase, pageSize, hasMinerUFilter);

        cancellationToken.ThrowIfCancellationRequested();
        var (results, totalCount, nextToken) = await searchService.ExactSearchAsync(
            query, phrase, filter, pageSize, pageToken);
        cancellationToken.ThrowIfCancellationRequested();

        var identities = results.Select(hit => Guid.TryParse(hit.SegmentId, out var blockId) && blockId != Guid.Empty
            ? (SearchBlockPageKey?)new(blockId, hit.PageNumber) : null).ToArray();
        var wanted = identities.Where(key => key.HasValue).Select(key => key!.Value).Distinct().ToArray();
        IReadOnlyDictionary<SearchBlockPageKey, string?> labels = new Dictionary<SearchBlockPageKey, string?>();
        if (wanted.Length > 0)
        {
            try { labels = await pageLabels.ReadBatchAsync(wanted, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning("Optional search page labels unavailable");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < results.Count; i++)
            results[i].PageLabel = identities[i] is { } key && labels.TryGetValue(key, out var label) ? label : null;

        logger.LogInformation("Search completed: results={Count}, total={Total}", results.Count, totalCount);

        var response = new
        {
            results = results.Select(r => new
            {
                documentName = r.DocumentName,
                pageNumber = r.PageNumber,
                pageLabel = r.PageLabel,
                associatedText = r.AssociatedText,
                score = r.Score,
                matchType = r.MatchType,
                segmentId = r.SegmentId,
                startOffset = r.StartOffset,
                endOffset = r.EndOffset,
                createdAt = r.CreatedAt?.ToString("o"),
                // [Gen-2] minerU optional fields (null when absent — backward compatible)
                blockData = r.BlockData,
                bbox = r.Bbox,
                mineruScore = r.MineruScore,
                subType = r.SubType,
                textLevel = r.TextLevel,
                textFormat = r.TextFormat,
                caption = r.Caption,
                // Hand-marked structure path (null when the block lies outside every marked node)
                structurePath = r.StructurePath
            }).ToList(),
            totalCount,
            nextPageToken = nextToken ?? string.Empty
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Results.Ok(response);
    }
}
