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
        var group = app.MapGroup("/admin/documents");

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
        [FromQuery] string? documentTitle = null)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentSearchEndpoints));

        if (string.IsNullOrWhiteSpace(query))
            return Results.BadRequest(new { success = false, message = "Query cannot be empty", errorCode = "DOCLIBRARY_QUERY_REQUIRED" });

        if (query.Length > 200)
            return Results.BadRequest(new { success = false, message = "Query exceeds 200 characters", errorCode = "DOCLIBRARY_QUERY_TOO_LONG" });

        pageSize = Math.Min(Math.Max(pageSize, 1), 100);

        SearchFilterModel? filter = null;
        if (!string.IsNullOrWhiteSpace(subject) || !string.IsNullOrWhiteSpace(grade)
            || !string.IsNullOrWhiteSpace(year) || !string.IsNullOrWhiteSpace(documentTitle))
        {
            filter = new SearchFilterModel
            {
                Subject = subject,
                Grade = grade,
                Year = year,
                DocumentTitle = documentTitle
            };
        }

        logger.LogInformation("Search request: query={Query}, phrase={Phrase}, pageSize={PageSize}", query, phrase, pageSize);

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
                createdAt = r.CreatedAt?.ToString("o")
            }),
            totalCount,
            nextPageToken = nextToken ?? string.Empty
        });
    }
}
