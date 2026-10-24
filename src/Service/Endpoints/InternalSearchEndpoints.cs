using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service;

public static class InternalSearchEndpoints
{
    public static WebApplication MapInternalSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/search", Search).AllowAnonymous()
            .WithMetadata(new InternalApiEndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        return app;
    }

    private static async Task<IResult> Search(HttpRequest request, IInternalStructureRepository structures,
        ISearchDomainService search, CancellationToken cancellationToken)
    {
        if (!InternalSearchInput.TryRead(request.Query, out var input))
            return Results.BadRequest(new { success = false, errorCode = "DOCTHECA_INTERNAL_INVALID", message = "Invalid internal API request." });
        cancellationToken.ThrowIfCancellationRequested();
        SearchFilterModel? filter = null;
        if (input.StructureNodeId is { } id)
        {
            var node = await structures.ReadCurrentNodeAsync(id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (node is null) return Results.NotFound(new
                { success = false, errorCode = "DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND", message = "Internal resource not found." });
            if (node.ParseId is null)
                return Results.Ok(new { success = true, data = new InternalSearchPage([], 0, null) });
            // Node/parse ownership is resolved for this request. The derived index can lag a
            // subsequent committed tree edit; this does not create a cross-request lease.
            filter = new SearchFilterModel { StructureNodeId = node.NodeId, ParseId = node.ParseId };
        }
        var (results, total, token) = await search.ExactSearchAsync(input.Query, input.Phrase, filter, input.PageSize, input.PageToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (token is not null && !InternalSearchInput.ValidToken(token))
            throw new InvalidOperationException("Invalid search continuation response.");
        var page = new InternalSearchPage(results.Select(hit => new InternalSearchHit(hit.DocumentName, hit.PageNumber,
            hit.AssociatedText, hit.Score, hit.MatchType, hit.SegmentId, hit.StartOffset, hit.EndOffset, hit.StructurePath)).ToList(), total, token);
        return Results.Ok(new { success = true, data = page });
    }
}
