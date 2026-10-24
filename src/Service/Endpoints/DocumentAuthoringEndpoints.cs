using System.Security.Claims;
using System.Text.Json;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Domain.Services.Structure;
using Doctheca.Service.Parsing;
using Doctheca.Service.StructaDoc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Doctheca.Service;

internal static class DocumentAuthoringEndpoints
{
    private static readonly JsonSerializerOptions TreeJson = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    internal static void MapAuthoring(this RouteGroupBuilder group)
    {
        group.MapPost("/", Create);
        group.MapPost("/{id:guid}/source", Attach).WithMetadata(new RequestSizeLimitAttribute(200 * 1024 * 1024));
        group.MapGet("/{id:guid}/structure", GetTree);
        group.MapPut("/{id:guid}/structure", SaveTree);
    }
    internal sealed record MetadataRequest(string? FileName, string? Subject, string? Grade, string? Year, string? StructureDefinitionId);
    internal sealed record TreeRequest(List<DocumentNodeInput>? Nodes);

    private static async Task<IResult> Create(HttpRequest request, IDocumentAuthoringRepository repository, CancellationToken ct)
    {
        MetadataRequest? body;
        try { body = await request.ReadFromJsonAsync<MetadataRequest>(ct); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return StructureHttpResults.Invalid([new("body", "must be valid document JSON")]); }
        if (body is null) return StructureHttpResults.Invalid([new("body", "is required")]);
        if (!TryMetadata(body, out var input)) return InvalidDefinition();
        var subject = request.HttpContext.User.FindFirst("sub")?.Value ?? request.HttpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var result = await repository.CreateAsync(input!, Guid.TryParse(subject, out var user) ? user : null, ct);
        return result.Status == StructureResultStatus.Ok
            ? Results.Created($"/admin/document-files/{result.Value!.Id}", new { success = true, data = Project(result.Value) })
            : StructureHttpResults.From(result, Project);
    }

    internal static bool TryMetadata(MetadataRequest body, out DocumentMetadataInput? input)
    {
        input = null; Guid? definition = null;
        if (!string.IsNullOrEmpty(body.StructureDefinitionId))
        {
            if (!Guid.TryParse(body.StructureDefinitionId, out var id)) return false;
            definition = id;
        }
        input = new(body.FileName, body.Subject, body.Grade, body.Year, body.StructureDefinitionId is not null, definition);
        return true;
    }
    internal static IResult InvalidDefinition() => StructureHttpResults.Invalid([new("structureDefinitionId", "must be a definition id or empty string")]);
    internal static object Project(DocumentFileModel f) => new { id = f.Id, fileName = f.FileName, contentType = f.ContentType,
        subject = f.Subject, grade = f.Grade, year = f.Year, structureDefinitionId = f.StructureDefinitionId,
        hasSource = f.HasSource, sourceState = f.SourceState, createdAt = f.CreatedAt, updatedAt = f.UpdatedAt };

    private static async Task<IResult> GetTree(Guid id, IDocumentAuthoringRepository repository, CancellationToken ct)
    {
        var tree = await repository.GetTreeAsync(id, ct);
        return tree is null ? Results.NotFound(new { success = false, errorCode = StructureErrorCodes.FileNotFound, message = "File not found" })
            : Results.Json(new { success = true, data = StructureHttpResults.Tree(tree) }, TreeJson);
    }
    private static async Task<IResult> SaveTree(Guid id, HttpRequest request, IDocumentAuthoringRepository repository,
        ParseSearchIndexer indexer, CancellationToken ct)
    {
        TreeRequest? body;
        try { body = await request.ReadFromJsonAsync<TreeRequest>(TreeJson, ct); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return StructureHttpResults.InvalidBody(); }
        if (body?.Nodes is null) return StructureHttpResults.Invalid([new("nodes", "is required")]);
        var result = await repository.SaveTreeAsync(id, body.Nodes, ct);
        if (result.Status != StructureResultStatus.Ok) return StructureHttpResults.From(result, r => StructureHttpResults.Tree(r.Tree));
        var indexed = true;
        foreach (var parseId in result.Value!.AffectedParseIds) indexed &= await indexer.ReindexAsync(parseId);
        return Results.Json(new { success = true, data = StructureHttpResults.Tree(result.Value.Tree), searchIndexUpdated = indexed }, TreeJson);
    }

    private static async Task<IResult> Attach(Guid id, HttpRequest request, IDocumentFileService files,
        IDocumentAuthoringRepository repository, IStructaDocClient upstream, IOptions<StructaDocOptions> options,
        IServiceScopeFactory scopes, ILoggerFactory logs, CancellationToken ct)
    {
        var logger = logs.CreateLogger(nameof(DocumentAuthoringEndpoints));
        var book = await files.GetByIdAsync(id);
        if (book is null) return Results.NotFound(new { success = false, message = "File not found", errorCode = StructureErrorCodes.FileNotFound });
        if (book.HasSource) return SourceExists();
        if (!request.HasFormContentType) return StructureHttpResults.Invalid([new("file", "multipart/form-data is required")]);
        IFormCollection form;
        try { form = await request.ReadFormAsync(ct); }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        { return StructureHttpResults.Invalid([new("file", "invalid or oversized multipart file")]); }
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return StructureHttpResults.Invalid([new("file", "must not be empty")]);
        if (file.Length > DocumentFileUploadEndpoint.MaxFileSize) return StructureHttpResults.Invalid([new("file", "must not exceed 200 MB")]);
        if (!DocumentFileUploadEndpoint.DocumentFileMimeTypes.Contains(file.ContentType))
            return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCTHECA_FILE_FORMAT_UNSUPPORTED" });
        if (!options.Value.IsConfigured) return Results.Json(new { success = false, message = "StructaDoc service is not configured", errorCode = "DOCTHECA_STRUCTADOC_NOT_CONFIGURED" }, statusCode: 503);
        Guid? uploaded = null;
        try
        {
            // No document transaction is held while streaming to the versioned scoped API.
            await using (var stream = file.OpenReadStream())
                uploaded = (await upstream.UploadDocumentAsync(file.FileName, file.ContentType, stream, ct)).Id;
            if (uploaded == Guid.Empty) { uploaded = null; throw new StructaDocException("Invalid source identity", 502, false); }
            var result = await repository.AttachSourceAsync(id, uploaded.Value, file.ContentType, ct);
            if (result.Status == StructureResultStatus.Ok) return StructureHttpResults.From(result, Project);
            await CleanupAsync(uploaded.Value);
            return StructureHttpResults.From(result, Project);
        }
        catch (StructaDocException ex)
        {
            logger.LogWarning("First source upload was rejected for document {DocumentId}", id);
            if (uploaded.HasValue) await CleanupAsync(uploaded.Value);
            if (ex.StatusCode == 413 || ex.ProblemCode == "file-too-large") return StructureHttpResults.Invalid([new("file", "exceeds the upstream upload limit")]);
            if (ex.StatusCode == 415 || ex.ProblemCode == "unsupported-document-type") return Results.BadRequest(new { success = false, message = "Unsupported file format", errorCode = "DOCTHECA_FILE_FORMAT_UNSUPPORTED" });
            return Results.Json(new { success = false, message = "Source upload failed", errorCode = ex.StatusCode is 401 or 403 ? "DOCTHECA_STRUCTADOC_UNAUTHORIZED" : "DOCTHECA_STRUCTADOC_ERROR" }, statusCode: 502);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (uploaded.HasValue) await CleanupAsync(uploaded.Value);
            throw;
        }
        catch (Exception)
        {
            // Exceptions and provider problem bodies may contain uploaded names, URLs or credentials.
            logger.LogWarning("First source attachment failed for document {DocumentId}", id);
            if (uploaded.HasValue) await CleanupAsync(uploaded.Value);
            return Results.Json(new { success = false, message = "Source attachment failed", errorCode = "DOCTHECA_STRUCTADOC_ERROR" }, statusCode: 502);
        }
        async Task CleanupAsync(Guid source)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var scope = scopes.CreateScope();
                // A fresh connection resolves commit uncertainty. Failure to prove no reference defers deletion.
                if (await scope.ServiceProvider.GetRequiredService<IDocumentAuthoringRepository>().IsSourceReferencedAsync(id, source, timeout.Token)) return;
                await upstream.DeleteDocumentAsync(source, timeout.Token);
            }
            catch (Exception) { logger.LogWarning("Unattached source cleanup deferred for document {DocumentId}", id); }
        }
    }
    private static IResult SourceExists() => Results.Conflict(new { success = false, message = "The document already has a source", errorCode = "DOCTHECA_DOCUMENT_SOURCE_EXISTS" });
}
