using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service.Endpoints.Models;

namespace Ruoyu.Study.DocLibrary.Service;

internal static class DocumentFileMetadataEndpoint
{
    internal static RouteHandlerBuilder MapMetadata(this RouteGroupBuilder group)
    {
        return group.MapPut("/{id:guid}/metadata", UpdateDocumentFileMetadata);
    }

    private static async Task<IResult> UpdateDocumentFileMetadata(
        Guid id,
        HttpRequest request,
        IDocumentFileService fileService,
        ISearchIndexService searchIndexService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileMetadataEndpoint));

        UpdateMetadataRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<UpdateMetadataRequest>();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse metadata update request for file {FileId}", id);
            return Results.BadRequest(new { success = false, message = "Invalid request body" });
        }

        if (body == null)
        {
            return Results.BadRequest(new { success = false, message = "Request body is required" });
        }

        var updated = await fileService.UpdateMetadataAsync(id, body.Subject, body.Grade, body.Year);
        if (updated == null)
        {
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCLIBRARY_FILE_NOT_FOUND" });
        }

        // Best-effort: sync OpenSearch index with new metadata
        try
        {
            await searchIndexService.UpdateDocumentFileMetadataAsync(id, updated.Subject, updated.Grade, updated.Year);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sync OpenSearch metadata for file {FileId}", id);
        }

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                id = updated.Id.ToString(),
                fileName = updated.FileName,
                subject = updated.Subject,
                grade = updated.Grade,
                year = updated.Year,
                updatedAt = updated.UpdatedAt
            }
        });
    }
}
