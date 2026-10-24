using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.InternalApi;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Minio.Exceptions;

namespace Doctheca.Service;

public static class InternalContentEndpoints
{
    public static WebApplication MapInternalContentEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/structure-nodes/{nodeId}/content", ReadContent)
            .AllowAnonymous().WithMetadata(new InternalApiEndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        // Binary streaming owns errors and aborts. It must stay outside the JSON middleware,
        // which intentionally swallows exceptions after a response has started.
        app.MapGet("/internal/v1/structure-nodes/{nodeId}/images/{imageId}/content", ReadImage)
            .AllowAnonymous().WithMetadata(new InternalApiEndpointMetadata());
        return app;
    }

    private static IResult Error(int status, string code, string message) => Results.Json(new
        { success = false, errorCode = code, message }, statusCode: status);
    private static IResult Invalid() => Error(400, "DOCTHECA_INTERNAL_INVALID", "Invalid internal API request.");
    private static IResult Missing() => Error(404, "DOCTHECA_INTERNAL_RESOURCE_NOT_FOUND", "Internal content not found.");
    private static IResult Changed() => Error(409, "DOCTHECA_INTERNAL_STRUCTURE_CHANGED", "Structure content changed; restart from the first page.");
    private static IResult Unavailable() => Error(503, "DOCTHECA_INTERNAL_IMAGE_UNAVAILABLE", "Image content is unavailable.");

    private static async Task<IResult> ReadContent(string nodeId, HttpRequest request,
        IInternalContentRepository repository, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(nodeId, "D", out var id) || !InternalContentInput.TryRead(request.Query, false, out var query)) return Invalid();
        var result = await repository.ReadContentAsync(id, query, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result.Status switch
        {
            InternalContentStatus.Found => Results.Ok(new { success = true, data = result.Page }),
            InternalContentStatus.Changed => Changed(),
            InternalContentStatus.InvalidCursor => Invalid(),
            _ => Missing(),
        };
    }

    private static Task ReadImage(string nodeId, string imageId, HttpContext context,
        IInternalContentRepository repository, ParseImageContentSource source, ILoggerFactory loggerFactory) =>
        SendImageAsync(nodeId, imageId, context, repository, source, loggerFactory.CreateLogger(nameof(InternalContentEndpoints)));

    private static async Task SendImageAsync(string nodeId, string imageId, HttpContext context,
        IInternalContentRepository repository, ParseImageContentSource source, ILogger logger)
    {
        var token = context.RequestAborted;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (!Guid.TryParseExact(nodeId, "D", out var node) || !Guid.TryParseExact(imageId, "D", out var image) ||
            !InternalContentInput.TryRead(context.Request.Query, true, out var query))
        {
            await Invalid().ExecuteAsync(context);
            return;
        }
        try
        {
            var read = await repository.ReadImageAsync(node, image, query.Revision!, token);
            token.ThrowIfCancellationRequested();
            if (read.Status != InternalContentStatus.Found)
            {
                await (read.Status == InternalContentStatus.Changed ? Changed() : Missing()).ExecuteAsync(context);
                return;
            }
            var mediaType = read.Image!.ContentType.ToLowerInvariant();
            if (mediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/avif" or "image/tiff"))
            {
                await Missing().ExecuteAsync(context);
                return;
            }
            await using var stream = await source.OpenAsync(read.Parse!, read.Image, token);
            token.ThrowIfCancellationRequested();
            if (stream is null)
            {
                await Missing().ExecuteAsync(context);
                return;
            }
            context.Response.ContentType = mediaType;
            await stream.CopyToAsync(context.Response.Body, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            context.Abort();
            throw new OperationCanceledException(token);
        }
        catch (Exception exception)
        {
            // Never attach the exception: provider messages may contain object keys or credentials.
            logger.LogWarning("Internal image read failed.");
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.ContentType = null;
            var missing = exception is FileNotFoundException or DirectoryNotFoundException or ObjectNotFoundException ||
                exception is StructaDocException { StatusCode: 404 };
            await (missing ? Missing() : Unavailable()).ExecuteAsync(context);
        }
    }
}
