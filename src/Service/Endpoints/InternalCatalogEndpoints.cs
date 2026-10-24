using Doctheca.Domain.Repositories;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service;

public sealed class InternalApiEndpointMetadata;

public static class InternalCatalogEndpoints
{
    public static WebApplication UseInternalApiBoundary(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase) &&
                context.GetEndpoint()?.Metadata.GetMetadata<InternalApiEndpointMetadata>() is null)
            {
                // Reserved internal errors, including invalid audio links, must never be cached or used as referrers.
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                await NotFound().ExecuteAsync(context);
                return;
            }
            await next(context);
        });
        return app;
    }

    public static WebApplication MapInternalCatalogEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1").AllowAnonymous()
            .WithMetadata(new InternalApiEndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        group.MapGet("/structure-definitions", Definitions);
        group.MapGet("/documents", Documents);
        return app;
    }

    public static IResult NotFound() => Results.NotFound(new
        { success = false, errorCode = "DOCTHECA_INTERNAL_NOT_FOUND", message = "Internal resource not found." });

    private static IResult Invalid() => Results.BadRequest(new
        { success = false, errorCode = "DOCTHECA_INTERNAL_INVALID", message = "Invalid internal API request." });

    private static async Task<IResult> Definitions(HttpRequest request, IInternalCatalogRepository repository, CancellationToken cancellationToken)
    {
        if (!InternalCatalogInput.TryRead(request.Query, false, out var query, out var identity)) return Invalid();
        var rows = await repository.ReadDefinitionsAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Results.Ok(new { success = true, data = new { items = rows.Take(query.PageSize),
            nextPageToken = rows.Count > query.PageSize ? InternalCatalogInput.Encode(rows[query.PageSize - 1].Id, identity) : null } });
    }

    private static async Task<IResult> Documents(HttpRequest request, IInternalCatalogRepository repository, CancellationToken cancellationToken)
    {
        if (!InternalCatalogInput.TryRead(request.Query, true, out var query, out var identity)) return Invalid();
        var page = await repository.ReadDocumentsAsync(query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!page.DefinitionExists) return Results.NotFound(new
            { success = false, errorCode = "DOCTHECA_INTERNAL_DEFINITION_NOT_FOUND", message = "Structure definition not found." });
        return Results.Ok(new { success = true, data = new { items = page.Items.Take(query.PageSize),
            nextPageToken = page.Items.Count > query.PageSize ? InternalCatalogInput.Encode(page.Items[query.PageSize - 1].Id, identity) : null } });
    }
}
