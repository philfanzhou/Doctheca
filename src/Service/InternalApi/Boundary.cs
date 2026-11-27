using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service.InternalApi;

/// <summary>Marks the endpoints that belong to the anonymous internal API under <c>/internal</c>.</summary>
public sealed class EndpointMetadata;

/// <summary>
/// Reserves every <c>/internal</c> path: a request that matches no marked endpoint answers the
/// fixed 404 before static files or the SPA fallback can handle it.
/// </summary>
public static class Boundary
{
    public static WebApplication Reserve(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase) &&
                context.GetEndpoint()?.Metadata.GetMetadata<EndpointMetadata>() is null)
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

    public static IResult NotFound() => Results.NotFound(new
        { success = false, errorCode = "DOCTHECA_INTERNAL_NOT_FOUND", message = "Internal resource not found." });
}
