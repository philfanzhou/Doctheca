using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Domain.Services.Structure;
using Doctheca.Service.Endpoints.Models;

namespace Doctheca.Service;

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
        IDocumentStructureService structureService,
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

        // The structure definition is validated and applied first, so a rejected choice leaves
        // every other metadata field untouched.
        if (body.StructureDefinitionId != null)
        {
            Guid? definitionId = null;
            if (body.StructureDefinitionId.Length > 0)
            {
                if (!Guid.TryParse(body.StructureDefinitionId, out var parsed))
                {
                    return StructureHttpResults.Invalid(
                        [new StructureFieldError("structureDefinitionId", "must be a structure definition id or an empty string")]);
                }
                definitionId = parsed;
            }

            var assigned = await structureService.SetDocumentDefinitionAsync(id, definitionId);
            if (assigned.Status != StructureResultStatus.Ok)
            {
                return StructureHttpResults.From(assigned, _ => new { });
            }
        }

        var updated = await fileService.UpdateMetadataAsync(id, body.Subject, body.Grade, body.Year);
        if (updated == null)
        {
            return Results.NotFound(new { success = false, message = "File not found", errorCode = "DOCTHECA_FILE_NOT_FOUND" });
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
                structureDefinitionId = updated.StructureDefinitionId?.ToString(),
                updatedAt = updated.UpdatedAt
            }
        });
    }
}
