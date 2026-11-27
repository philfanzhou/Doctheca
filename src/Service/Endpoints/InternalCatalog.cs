using Doctheca.Domain.Repositories;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service.Endpoints;

/// <summary>
/// Anonymous read-only catalogs under <c>/internal/v1</c>: structure definitions and the documents
/// that downstream services may list.
/// </summary>
public static class InternalCatalog
{
    public static WebApplication MapInternalCatalog(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1").AllowAnonymous()
            .WithMetadata(new EndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        group.MapGet("/structure-definitions", Definitions);
        group.MapGet("/documents", Documents);
        return app;
    }

    private static IResult Invalid() => Results.BadRequest(new
        { success = false, errorCode = "DOCTHECA_INTERNAL_INVALID", message = "Invalid internal API request." });

    private static async Task<IResult> Definitions(HttpRequest request, IInternalCatalogRepository repository, CancellationToken cancellationToken)
    {
        if (!CatalogInput.TryRead(request.Query, false, out var query, out var identity)) return Invalid();
        var rows = await repository.ReadDefinitionsAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Results.Ok(new { success = true, data = new { items = rows.Take(query.PageSize),
            nextPageToken = rows.Count > query.PageSize ? CatalogInput.Encode(rows[query.PageSize - 1].Id, identity) : null } });
    }

    private static async Task<IResult> Documents(HttpRequest request, IInternalCatalogRepository repository, CancellationToken cancellationToken)
    {
        if (!CatalogInput.TryRead(request.Query, true, out var query, out var identity)) return Invalid();
        var page = await repository.ReadDocumentsAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!page.DefinitionExists) return Results.NotFound(new
            { success = false, errorCode = "DOCTHECA_INTERNAL_DEFINITION_NOT_FOUND", message = "Structure definition not found." });
        return Results.Ok(new { success = true, data = new { items = page.Items.Take(query.PageSize),
            nextPageToken = page.Items.Count > query.PageSize ? CatalogInput.Encode(page.Items[query.PageSize - 1].Id, identity) : null } });
    }
}
