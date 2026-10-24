using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Doctheca.Domain.Models;
using Doctheca.Domain.Services;

namespace Doctheca.Service;

internal static class DocumentFileListEndpoint
{
    internal static RouteHandlerBuilder MapList(this RouteGroupBuilder group)
    {
        return group.MapGet("/", ListDocumentFiles);
    }

    private static async Task<IResult> ListDocumentFiles(
        IDocumentFileService fileService,
        IDocumentParseService parseService,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? parseStatus = null,
        [FromQuery] string? fileName = null,
        [FromQuery] string? sourceState = null)
    {
        if (sourceState is not null && sourceState is not "structure-only" and not "with-source")
            return Results.BadRequest(new { success = false, message = "Invalid sourceState" });
        // Fetch a larger set to allow in-memory filtering by parseStatus
        var fetchPage = 1;
        var fetchSize = string.IsNullOrEmpty(parseStatus) ? pageSize : 200;
        var allEnriched = new List<(DocumentFileModel File, string? ParseStatus, string? ErrorMessage, DateTimeOffset? ParsedAt)>();

        while (true)
        {
            var (items, totalCount) = await fileService.GetListAsync(fetchPage, fetchSize, fileName);
            foreach (var f in items)
            {
                var parse = await parseService.GetLatestByFileIdAsync(f.Id);
                allEnriched.Add((f, parse?.Status, parse?.ErrorMessage, parse?.ParsedAt));
            }
            if (items.Count < fetchSize || allEnriched.Count >= totalCount) break;
            fetchPage++;
        }

        // Filter by parseStatus
        IEnumerable<(DocumentFileModel File, string? ParseStatus, string? ErrorMessage, DateTimeOffset? ParsedAt)> filtered = allEnriched;
        if (!string.IsNullOrEmpty(parseStatus))
        {
            if (parseStatus == "unparsed")
                filtered = filtered.Where(x => x.ParseStatus == null);
            else
                filtered = filtered.Where(x => x.ParseStatus == parseStatus);
        }

        if (sourceState is not null) filtered = filtered.Where(x => x.File.SourceState == sourceState);
        var totalFiltered = filtered.Count();
        var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var data = paged.Select(f => new
        {
            id = f.File.Id.ToString(),
            fileName = f.File.FileName,
            contentType = f.File.ContentType,
            subject = f.File.Subject, grade = f.File.Grade, year = f.File.Year,
            structureDefinitionId = f.File.StructureDefinitionId,
            hasSource = f.File.HasSource, sourceState = f.File.SourceState,
            parseStatus = f.ParseStatus,
            errorMessage = f.ErrorMessage,
            createdBy = f.File.CreatedBy?.ToString(),
            createdAt = f.File.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            parsedAt = f.ParsedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        }).ToList();

        return Results.Ok(new
        {
            success = true,
            data,
            total = totalFiltered,
            page,
            pageSize,
            totalPages = (totalFiltered + pageSize - 1) / pageSize
        });
    }
}
