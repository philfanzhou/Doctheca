using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Service.Audio;
using Doctheca.Service.InternalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Doctheca.Service;

public static class InternalAudioEndpoints
{
    public static WebApplication MapInternalAudioEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/structure-nodes/{nodeId}/audio", List)
            .AllowAnonymous().WithMetadata(new InternalApiEndpointMetadata()).RequireServiceMantleSecurityResponseHeaders();
        // Binary delivery owns pre-start errors and post-start aborts, outside JSON Problem Details middleware.
        app.MapGet("/internal/v1/structure-nodes/{nodeId}/audio/{audioId}/content", Content)
            .AllowAnonymous().WithMetadata(new InternalApiEndpointMetadata());
        return app;
    }
    private static void Headers(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    private static IResult Error(int status, string code, string message) => Results.Json(new
        { success = false, errorCode = code, message }, statusCode: status);
    private static bool TryId(string value, out Guid id)
    { id = Guid.Empty; return value.Length == 36 && Guid.TryParseExact(value, "D", out id); }
    private static IResult Invalid() => Error(400, "DOCTHECA_INTERNAL_INVALID", "Invalid internal API request.");
    private static IResult Missing() => Error(404, "DOCTHECA_INTERNAL_AUDIO_NOT_FOUND", "Audio not found.");
    private static IResult Unavailable() => Error(503, "DOCTHECA_INTERNAL_AUDIO_UNAVAILABLE", "Audio is unavailable.");

    private static async Task<IResult> List(string nodeId, HttpContext context, IInternalAudioRepository repository,
        InternalAudioSettings settings, InternalAudioTokens tokens, TimeProvider clock)
    {
        Headers(context);
        if (!TryId(nodeId, out var node) || context.Request.Query.Count != 0) return Invalid();
        var read = await repository.ReadAudioAsync(node, null, context.RequestAborted);
        context.RequestAborted.ThrowIfCancellationRequested();
        if (read is null) return Error(404, "DOCTHECA_INTERNAL_NODE_NOT_FOUND", "Node not found.");
        if (!settings.Available) return Unavailable();
        var expiry = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds()).AddSeconds(settings.LinkSeconds);
        var items = read.Items.Select(audio => new InternalAudioItem(audio.Id,
            Path.GetFileName(audio.SourcePath.Replace('\\', '/')), audio.ContentType, audio.SizeBytes,
            new(context.Request.PathBase.ToUriComponent().TrimEnd('/') + $"/internal/v1/structure-nodes/{node:D}/audio/{audio.Id:D}/content" +
                QueryString.Create("token", tokens.Create(new(read.Node.DocumentFileId, read.Node.ParseId, node, audio.Id), expiry)),
                expiry, settings.LinkSeconds))).ToArray();
        return Results.Ok(new { success = true, data = new InternalAudioList(read.Node.DocumentFileId, read.Node.ParseId, node, items) });
    }

    internal static async Task Content(string nodeId, string audioId, HttpContext context, IInternalAudioRepository repository,
        InternalAudioSettings settings, InternalAudioTokens tokens, StructureAudioStore store,
        IOptions<DocumentAudioOptions> options, AudioScratchSpace scratch, ILoggerFactory loggerFactory)
    {
        Headers(context);
        var cancellation = context.RequestAborted;
        var logger = loggerFactory.CreateLogger(nameof(InternalAudioEndpoints));
        try
        {
            if (!TryId(nodeId, out var node) || !TryId(audioId, out var audio) ||
                context.Request.Query.Keys.Any(key => key != "token") || context.Request.Query["token"].Count > 1)
            { await Invalid().ExecuteAsync(context); return; }
            var token = context.Request.Query["token"].ToString();
            if (!tokens.TryRead(token, node, audio, out var binding)) { await Missing().ExecuteAsync(context); return; }
            var before = await repository.ReadAudioAsync(node, audio, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var row = Match(before, binding!);
            if (row is null) { await Missing().ExecuteAsync(context); return; }
            if (!settings.Available) { await Unavailable().ExecuteAsync(context); return; }
            await using var stream = await store.BufferAsync(row, options.Value.MaxFileBytes, scratch, cancellation);
            // Token expiry and committed revocation are checked again after slow I/O, before any response bytes.
            if (!tokens.TryRead(token, node, audio, out binding)) { await Missing().ExecuteAsync(context); return; }
            var admitted = await repository.ReadAudioAsync(node, audio, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (Match(admitted, binding!) != row || !tokens.TryRead(token, node, audio, out _))
            { await Missing().ExecuteAsync(context); return; }
            try { await Results.Stream(stream, row.ContentType, enableRangeProcessing: true).ExecuteAsync(context); }
            catch (IOException) { throw new AudioStorageException(); }
            cancellation.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        { context.Abort(); throw new OperationCanceledException(cancellation); }
        catch (Exception exception)
        {
            logger.LogWarning("Internal audio read failed.");
            if (context.Response.HasStarted) { context.Abort(); return; }
            context.Response.ContentType = null;
            context.Response.ContentLength = null;
            if (exception is AudioStorageException) await Unavailable().ExecuteAsync(context);
            else await Results.Problem(statusCode: 500, title: "An unexpected error occurred.").ExecuteAsync(context);
        }
    }
    private static InternalAudioObject? Match(InternalAudioMembership? read, InternalAudioBinding binding) =>
        read?.Node.DocumentFileId == binding.DocumentFileId && read.Node.ParseId == binding.ParseId && read.Node.NodeId == binding.NodeId
            ? read.Items.SingleOrDefault(audio => audio.Id == binding.AudioId && audio.ParseId == binding.ParseId && audio.NodeId == binding.NodeId) : null;
}
