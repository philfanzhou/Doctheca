using System.Text.Json;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service;

public static class InternalStructureEndpoints
{
    // A valid definition can have 50 nested levels, each with a node object and children array.
    private static readonly JsonSerializerOptions TreeJsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static WebApplication MapInternalStructureEndpoints(this WebApplication app, InternalApiOptions options)
    {
        if (!options.Enabled) return app;
        app.MapGet("/internal/v1/documents/{documentFileId}/structure", ReadTree)
            .AllowAnonymous().WithMetadata(new InternalApiEndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        return app;
    }

    private static async Task<IResult> ReadTree(string documentFileId, HttpRequest request,
        IInternalStructureRepository repository, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(documentFileId, "D", out var id) || request.Query.Count != 0)
            return Results.BadRequest(new { success = false, errorCode = "DOCTHECA_INTERNAL_INVALID", message = "Invalid internal API request." });
        var result = await repository.ReadTreeAsync(id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Status switch
        {
            InternalStructureReadStatus.Found => Results.Json(new { success = true, data = result.Tree }, TreeJsonOptions),
            InternalStructureReadStatus.DocumentNotFound => Results.NotFound(new
                { success = false, errorCode = "DOCTHECA_INTERNAL_DOCUMENT_NOT_FOUND", message = "Document not found." }),
            _ => Results.NotFound(new
                { success = false, errorCode = "DOCTHECA_INTERNAL_STRUCTURE_NOT_FOUND", message = "Document has no marked structure." }),
        };
    }
}
