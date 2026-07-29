using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;

namespace Ruoyu.Study.DocLibrary.Service;

public static class DocumentSearchEndpoints
{
    public static WebApplication MapDocumentSearchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/documents")
            .RequireAuthorization(DocLibraryAuthorizationPolicies.Admin);

        group.MapGet("/search", Search);

        return app;
    }

    private static async Task<IResult> Search(
        ISearchDomainService searchService,
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
        [FromQuery] bool? hasImage = null)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentSearchEndpoints));

        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { success = false, message = "Query cannot be empty", errorCode = "DOCLIBRARY_QUERY_REQUIRED" });

        if (query.Length > 200)
            return Results.BadRequest(new { success = false, message = "Query exceeds 200 characters", errorCode = "DOCLIBRARY_QUERY_TOO_LONG" });

        pageSize = Math.Min(Math.Max(pageSize, 1), 100);

        // Build filter: V1 fields + [Gen-2] minerU fields (all null → filter stays null → zero regression)
        SearchFilterModel? filter = null;
        var hasV1Filter = !string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(grade)
            || !string.IsNullOrWhiteSpace(year) || !string.IsNullOrWhiteSpace(documentTitle);
        var hasMinerUFilter = !string.IsNullOrWhiteSpace(blockType) || !string.IsNullOrWhiteSpace(blockSubType)
            || pageNumber.HasValue || textLevel.HasValue || !string.IsNullOrWhiteSpace(textFormat)
            || parseId.HasValue || documentFileId.HasValue || hasImage.HasValue;

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
                HasImage = hasImage
            };
        }

        logger.LogInformation("Search request: query={Query}, phrase={Phrase}, pageSize={PageSize}, hasMinerUFilter={HasMinerUFilter}",
            query, phrase, pageSize, hasMinerUFilter);

        var (results, totalCount, nextToken) = await searchService.ExactSearchAsync(
            query, phrase, filter, pageSize, pageToken);

        logger.LogInformation("Search completed: query={Query}, results={Count}, total={Total}", query, results.Count, totalCount);

        return Results.Ok(new
        {
            results = results.Select(r => new
            {
                documentName = r.DocumentName,
                pageNumber = r.PageNumber,
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
                caption = r.Caption
            }),
            totalCount,
            nextPageToken = nextToken ?? string.Empty
        });
    }
}
