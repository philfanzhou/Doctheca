using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Service.Audio;
using Doctheca.Domain.Services.Structure;
using System.Text.Json;

namespace Doctheca.Service;

/// <summary>
/// Structure audio of a parse (issue #90): import with pre-filled links, review and change links,
/// remove files. All JSON, all behind
/// <c>DocthecaAdmin</c>. Only the temporary playback URL carries a public storage location and signature.
/// </summary>
public static class StructureAudioEndpoints
{
    /// <summary>Room for multipart boundaries and headers on top of the uploaded bytes.</summary>
    private const long MultipartOverheadBytes = DocumentAudioOptions.MultipartOverheadBytes;

    public static WebApplication MapStructureAudioEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<DocumentAudioOptions>>().Value;
        var parses = app.MapGroup("/admin/document-parses")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        parses.MapGet("/{parseId:guid}/audio", List);
        parses.MapGet("/{parseId:guid}/structure/nodes/{nodeId:guid}/audio", ListNode);
        parses.MapGet("/{parseId:guid}/audio/{audioId:guid}/playback-link", Playback);
        parses.MapPost("/{parseId:guid}/audio", Import)
            .WithMetadata(new RequestSizeLimitAttribute(checked(options.MaxArchiveBytes + MultipartOverheadBytes)));
        parses.MapPut("/{parseId:guid}/audio", Save);
        parses.MapDelete("/{parseId:guid}/audio/{audioId:guid}", Remove);

        var documents = app.MapGroup("/admin/document-files").RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        documents.MapGet("/{documentFileId:guid}/audio", (Guid documentFileId, StructureAudioService service, IOptions<DocumentAudioOptions> limits) => ListOwner(new(documentFileId, true), service, limits));
        documents.MapGet("/{documentFileId:guid}/structure/nodes/{nodeId:guid}/audio", (Guid documentFileId, Guid nodeId, StructureAudioService service, HttpContext context) =>
            ListNodeOwner(new(documentFileId, true), nodeId, service, context));
        documents.MapGet("/{documentFileId:guid}/audio/{audioId:guid}/playback-link", (Guid documentFileId, Guid audioId, StructureAudioService service, HttpContext context) =>
            PlaybackOwner(new(documentFileId, true), audioId, service, context));
        documents.MapPost("/{documentFileId:guid}/audio", (Guid documentFileId, HttpRequest request, StructureAudioService service, IOptions<DocumentAudioOptions> limits, ILoggerFactory logs) =>
            ImportOwner(new(documentFileId, true), request, service, limits, logs))
            .WithMetadata(new RequestSizeLimitAttribute(checked(options.MaxArchiveBytes + MultipartOverheadBytes)));
        documents.MapPut("/{documentFileId:guid}/audio", (Guid documentFileId, HttpRequest request, StructureAudioService service, ILoggerFactory logs) =>
            SaveOwner(new(documentFileId, true), request, service, logs));
        documents.MapDelete("/{documentFileId:guid}/audio/{audioId:guid}", (Guid documentFileId, Guid audioId, StructureAudioService service, HttpContext context) =>
            RemoveOwner(new(documentFileId, true), audioId, service, context));

        var audio = app.MapGroup("/admin/structure-audio")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        audio.MapPost("/orphan-sweep", Sweep);

        return app;
    }

    /// <summary>Request body of PUT audio: link changes (null node clears) and files to remove.</summary>
    public sealed record SaveAudioRequest(List<LinkRequest?>? Links, List<Guid>? RemoveIds);

    public sealed record LinkRequest(Guid? Id, Guid? NodeId);

    private static Task<IResult> List(Guid parseId, StructureAudioService service, IOptions<DocumentAudioOptions> limits) => ListOwner(new(parseId), service, limits);
    private static async Task<IResult> ListOwner(AudioOwner owner, StructureAudioService service, IOptions<DocumentAudioOptions> limits) =>
        From(await service.ListAsync(owner), list => new { parseId = list.ParseId?.ToString(), documentFileId = list.DocumentFileId.ToString(),
            treeId = list.TreeId?.ToString(), storage = Storage(service.StorageStatus, limits.Value), items = Items(list) });
    private static Task<IResult> ListNode(Guid parseId, Guid nodeId, StructureAudioService service, HttpContext context) => ListNodeOwner(new(parseId), nodeId, service, context);
    private static async Task<IResult> ListNodeOwner(AudioOwner owner, Guid nodeId, StructureAudioService service, HttpContext context) =>
        From(await service.ListNodeAsync(owner, nodeId, context.RequestAborted), list => new { parseId = list.ParseId?.ToString(),
            documentFileId = list.DocumentFileId.ToString(), nodeId = nodeId.ToString(), items = Items(list) });
    private static Task<IResult> Playback(Guid parseId, Guid audioId, StructureAudioService service, HttpContext context) => PlaybackOwner(new(parseId), audioId, service, context);
    private static async Task<IResult> PlaybackOwner(AudioOwner owner, Guid audioId, StructureAudioService service, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return From(await service.PlaybackAsync(owner, audioId, context.RequestAborted), link => new { url = link.Url, expiresAt = link.ExpiresAt, expiresInSeconds = link.ExpiresInSeconds });
    }
    private static Task<IResult> Import(Guid parseId, HttpRequest request, StructureAudioService audioService,
        IOptions<DocumentAudioOptions> options, ILoggerFactory loggerFactory) => ImportOwner(new(parseId), request, audioService, options, loggerFactory);

    private static async Task<IResult> ImportOwner(
        AudioOwner owner,
        HttpRequest request,
        StructureAudioService audioService,
        IOptions<DocumentAudioOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(StructureAudioEndpoints));
        var limits = options.Value;

        var preflight = await audioService.PreflightAsync(owner, request.HttpContext.RequestAborted);
        if (preflight.Status != AudioResultStatus.Ok) return From(preflight, _ => new { });
        if (!request.HasFormContentType)
            return Error(StatusCodes.Status400BadRequest, AudioErrorCodes.ImportInvalid, "Request must be multipart/form-data");

        var requestLimit = checked(limits.MaxArchiveBytes + MultipartOverheadBytes);
        if (request.ContentLength > requestLimit)
            return Error(StatusCodes.Status413PayloadTooLarge, AudioErrorCodes.ImportTooLarge, "The import exceeds the request limit");
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            sizeFeature.MaxRequestBodySize = requestLimit;
        var originalBody = request.Body;
        request.Body = new LimitedBodyStream(originalBody, requestLimit);
        IFormCollection form;
        try
        {
            // Each multipart section (one file or one archive) may be as large as the whole import.
            request.HttpContext.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
            {
                MultipartBodyLengthLimit = limits.MaxArchiveBytes,
                ValueCountLimit = DocumentAudioOptions.MaxFilesPerImport * 2 + 16,
            }));
            form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
            || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge))
        {
            logger.LogWarning("Rejected an oversized or malformed audio import for owner {OwnerId}", owner.Id);
            return ex is BadHttpRequestException || ex.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase)
                ? Error(StatusCodes.Status413PayloadTooLarge, AudioErrorCodes.ImportTooLarge,
                    $"The import exceeds the {AudioImportReader.FormatBytes(limits.MaxArchiveBytes)} limit")
                : Error(StatusCodes.Status400BadRequest, AudioErrorCodes.ImportInvalid, "The multipart body could not be read");
        }

        finally { request.Body = originalBody; }

        using var batch = await AudioImportReader.ReadAsync(form, limits, request.HttpContext.RequestAborted);
        var preview = request.Query["preview"];
        if (preview.Count > 1 || (preview.Count == 1 && preview[0] is not ("true" or "false")))
            return Error(400, AudioErrorCodes.Invalid, "preview must be true or false");
        if (preview.Count == 1 && preview[0] == "true")
            return From(await audioService.PreviewAsync(owner, batch, limits, request.HttpContext.RequestAborted), outcome => new
            { items = outcome.Items.Select(i => new { sourcePath = i.SourcePath, contentType = i.ContentType, sizeBytes = i.SizeBytes,
                matchStatus = i.MatchStatus, suggestedNodeId = i.SuggestedNodeId?.ToString(), suggestedNodePath = i.SuggestedNodePath }), skipped = outcome.Skipped });
        try
        {
            if (new[] { "targetNodeId", "targets", "links", "removeIds" }.Any(key => form[key].Count > 1))
                return Error(400, AudioErrorCodes.Invalid, "Each selection field must occur at most once");
            Guid? common = null;
            if (form["targetNodeId"].Count > 0 && form["targetNodeId"].ToString() is not ("" or "null"))
            {
                if (!Guid.TryParse(form["targetNodeId"].ToString(), out var id)) return Error(400, AudioErrorCodes.Invalid, "Invalid targetNodeId");
                common = id;
            }
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var targets = form["targets"].Count == 0 ? [] : JsonSerializer.Deserialize<List<AudioTarget>>(form["targets"].ToString(), json) ?? throw new JsonException();
            var links = form["links"].Count == 0 ? [] : JsonSerializer.Deserialize<List<LinkRequest?>>(form["links"].ToString(), json) ?? throw new JsonException();
            var removes = form["removeIds"].Count == 0 ? [] : JsonSerializer.Deserialize<List<Guid>>(form["removeIds"].ToString(), json) ?? throw new JsonException();
            if (targets.Count > DocumentAudioOptions.MaxFilesPerImport || links.Any(l => l?.Id is null) || links.GroupBy(l => l!.Id).Any(g => g.Count() > 1))
                return Error(400, AudioErrorCodes.Invalid, "Invalid or duplicate file selections");
            return From(await audioService.ImportAsync(owner, batch, limits, common, targets,
                links.Select(l => new AudioLinkChange(l!.Id!.Value, l.NodeId)).ToList(), removes.Distinct().ToArray(), request.HttpContext.RequestAborted), Imported);
        }
        catch (JsonException) { return Error(400, AudioErrorCodes.Invalid, "Invalid target or review selections"); }
    }

    private static Task<IResult> Save(Guid parseId, HttpRequest request, StructureAudioService audioService, ILoggerFactory loggerFactory) =>
        SaveOwner(new(parseId), request, audioService, loggerFactory);

    private static async Task<IResult> SaveOwner(AudioOwner owner, HttpRequest request, StructureAudioService audioService, ILoggerFactory loggerFactory)
    {
        SaveAudioRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<SaveAudioRequest>(request.HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            loggerFactory.CreateLogger(nameof(StructureAudioEndpoints))
                .LogWarning("Rejected an unreadable audio link request body for owner {OwnerId}", owner.Id);
            return Error(StatusCodes.Status400BadRequest, AudioErrorCodes.Invalid, "Invalid request body");
        }

        if (body == null) return Error(StatusCodes.Status400BadRequest, AudioErrorCodes.Invalid, "Invalid request body");
        var links = body.Links ?? [];
        var errors = new List<object>();
        for (var i = 0; i < links.Count; i++)
        {
            if (links[i]?.Id is null) errors.Add(new { field = $"links[{i}].id", reason = "is required" });
        }
        var changes = links.Where(link => link?.Id is not null).Select(link => new AudioLinkChange(link!.Id!.Value, link.NodeId)).ToList();
        if (changes.GroupBy(change => change.AudioId).Any(group => group.Count() > 1))
            errors.Add(new { field = "links", reason = "lists a file more than once" });
        if (errors.Count > 0)
            return Results.BadRequest(new { success = false, message = "Audio link validation failed", errorCode = AudioErrorCodes.Invalid, errors });

        var result = await audioService.UpdateAsync(owner, changes, (body.RemoveIds ?? []).Distinct().ToList(), false, request.HttpContext.RequestAborted);
        return From(result, list => new { parseId = list.ParseId?.ToString(), documentFileId = list.DocumentFileId.ToString(), items = Items(list) });
    }

    private static Task<IResult> Remove(Guid parseId, Guid audioId, StructureAudioService audioService, HttpContext context) => RemoveOwner(new(parseId), audioId, audioService, context);
    private static async Task<IResult> RemoveOwner(AudioOwner owner, Guid audioId, StructureAudioService audioService, HttpContext context)
    {
        var result = await audioService.UpdateAsync(owner, [], [audioId], true, context.RequestAborted);
        return From(result, _ => new { id = audioId.ToString(), deleted = true });
    }

    private static async Task<IResult> Sweep(StructureAudioService audioService)
    {
        var result = await audioService.SweepOrphansAsync();
        return From(result, outcome => new { scanned = outcome.Scanned, deleted = outcome.Deleted, failed = outcome.Failed });
    }

    private static object Imported(AudioImportOutcome outcome) => new
    {
        parseId = outcome.Imported.ParseId?.ToString(),
        documentFileId = outcome.Imported.DocumentFileId.ToString(),
        imported = outcome.Imported.Items.Count,
        linked = outcome.Imported.Items.Count(item => item.NodeId != null),
        flagged = outcome.Imported.Items.Count(item => item.NodeId == null),
        items = Items(outcome.Imported),
        skipped = outcome.Skipped,
    };

    private static object Storage(DocumentAudioStorageStatus status, DocumentAudioOptions options) => new
    {
        available = status.Available,
        reason = status.Reason,
        maxFileBytes = options.MaxFileBytes,
        maxArchiveBytes = options.MaxArchiveBytes,
        maxFiles = DocumentAudioOptions.MaxFilesPerImport,
        maxArchiveEntries = DocumentAudioOptions.MaxArchiveEntries,
        maxPathLength = AudioImportReader.MaxPathLength,
        maxSegmentLength = AudioImportReader.MaxSegmentLength,
        multipartOverheadBytes = MultipartOverheadBytes,
        extensions = AudioImportReader.Extensions,
    };

    /// <summary>The public projection of audio rows; the object key is deliberately absent.</summary>
    private static List<object> Items(ParseAudio list)
    {
        var policy = new AudioRequiredPolicy(list.Levels ?? [], list.Nodes);
        return list.Items.Select(item =>
        {
            var slash = item.SourcePath.LastIndexOf('/');
            var node = item.NodeId is { } nodeId ? list.Nodes.GetValueOrDefault(nodeId) : null;
            return (object)new
            {
                id = item.Id.ToString(),
                sourcePath = item.SourcePath,
                folder = slash < 0 ? "" : item.SourcePath[..slash],
                fileName = slash < 0 ? item.SourcePath : item.SourcePath[(slash + 1)..],
                contentType = item.ContentType,
                sizeBytes = item.SizeBytes,
                nodeId = item.NodeId?.ToString(),
                nodePath = node == null ? null : NodePath(node, list.Nodes),
                matchStatus = item.MatchStatus,
                linkSource = item.LinkSource,
                requiresReview = policy.Reason(item.NodeId) is not null,
                reviewReason = policy.Reason(item.NodeId),
                createdAt = item.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                updatedAt = item.UpdatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            };
        }).ToList();
    }

    private static string NodePath(DocumentStructureNodeModel node, IReadOnlyDictionary<Guid, DocumentStructureNodeModel> nodes)
    {
        var names = new List<string>();
        for (var cursor = node; cursor != null; cursor = cursor.ParentId is { } parentId ? nodes.GetValueOrDefault(parentId) : null)
            names.Insert(0, cursor.Name);
        return string.Join(" > ", names);
    }

    private static IResult From<T>(AudioResult<T> result, Func<T, object> project)
    {
        if (result.Status == AudioResultStatus.Ok) return Results.Ok(new { success = true, data = project(result.Value!) });
        var status = result.Status switch
        {
            AudioResultStatus.NotFound => StatusCodes.Status404NotFound,
            AudioResultStatus.Conflict => StatusCodes.Status409Conflict,
            AudioResultStatus.TooLarge => StatusCodes.Status413PayloadTooLarge,
            AudioResultStatus.StorageNotConfigured or AudioResultStatus.StorageUnavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest,
        };
        return Results.Json(new
        {
            success = false,
            message = result.Message,
            errorCode = result.ErrorCode,
            errors = result.Errors?.Select(error => new { file = error.File, reason = error.Reason }),
        }, statusCode: status);
    }

    private static IResult Error(int status, string errorCode, string message) =>
        Results.Json(new { success = false, message, errorCode }, statusCode: status);
    /// <summary>Bounds the complete multipart body even when Content-Length is absent.</summary>
    private sealed class LimitedBodyStream(Stream inner, long limit) : Stream
    {
        private long _read;
        private int Count(int count)
        {
            _read += count;
            if (_read > limit) throw new BadHttpRequestException("Audio request limit exceeded", StatusCodes.Status413PayloadTooLarge);
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}
