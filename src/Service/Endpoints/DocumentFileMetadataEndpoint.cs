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
using Doctheca.Service.Parsing;

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
        IDocumentAuthoringRepository repository,
        ISearchIndexService searchIndexService,
        IDocumentParseService parseService,
        ParseSearchIndexer searchIndexer,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentFileMetadataEndpoint));

        UpdateMetadataRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<UpdateMetadataRequest>();
        }
        catch (Exception)
        {
            logger.LogWarning("Failed to parse metadata update request for file {FileId}", id);
            return Results.BadRequest(new { success = false, message = "Invalid request body" });
        }

        if (body == null)
        {
            return Results.BadRequest(new { success = false, message = "Request body is required" });
        }

        if (!DocumentAuthoringEndpoints.TryMetadata(new(body.FileName, body.Subject, body.Grade, body.Year, body.StructureDefinitionId), out var input))
            return DocumentAuthoringEndpoints.InvalidDefinition();
        var result = await repository.UpdateAsync(id, input!, request.HttpContext.RequestAborted);
        if (result.Status != StructureResultStatus.Ok) return StructureHttpResults.From(result, DocumentAuthoringEndpoints.Project);
        var updated = result.Value!;

        // Best-effort: sync OpenSearch index with new metadata
        try
        {
            if (updated.HasSource)
            {
                await searchIndexService.UpdateDocumentFileMetadataAsync(id, updated.Subject, updated.Grade, updated.Year);
                if (body.FileName is not null)
                    foreach (var parse in (await parseService.GetByFileIdAsync(id)).Where(p => p.Status == Doctheca.Domain.Models.DocumentParseStatus.Parsed))
                        await searchIndexer.ReindexAsync(parse.Id);
            }
        }
        catch (Exception)
        {
            logger.LogWarning("Failed to sync OpenSearch metadata for file {FileId}", id);
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
