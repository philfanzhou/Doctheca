using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Doctheca.Common.Oss;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;
using Doctheca.Domain.Services.Structure;
using Doctheca.Service.Parsing;

namespace Doctheca.Service;

/// <summary>
/// Ordered blocks of a parse, its hand-marked structure tree, the blocks of one node, and the
/// search re-index repair endpoints (issue #89). All JSON, all behind <c>DocthecaAdmin</c>.
/// </summary>
public static class DocumentStructureEndpoints
{
    internal const int DefaultBlockLimit = 200;
    internal const int MaxBlockLimit = 1000;

    public static WebApplication MapDocumentStructureEndpoints(this WebApplication app)
    {
        var parses = app.MapGroup("/admin/document-parses")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        parses.MapGet("/{parseId:guid}/blocks", GetBlocks);
        parses.MapGet("/{parseId:guid}/structure", GetStructure);
        parses.MapPut("/{parseId:guid}/structure", SaveStructure);
        parses.MapGet("/{parseId:guid}/structure/nodes/{nodeId:guid}/blocks", GetNodeBlocks);
        parses.MapPost("/{parseId:guid}/search-index", ReindexParse);

        var searchIndex = app.MapGroup("/admin/search-index")
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin)
            .RequireServiceMantleSecurityResponseHeaders();
        searchIndex.MapPost("/reindex", ReindexAll);

        return app;
    }

    /// <summary>Request body of PUT structure: the whole tree, children nested.</summary>
    public sealed record SaveStructureRequest(List<NodeRequest?>? Nodes);

    public sealed record NodeRequest(
        Guid? Id, Guid? LevelId, string? Name, int? StartSequence, int? EndSequence, List<NodeRequest?>? Children);

    private static async Task<IResult> GetBlocks(
        Guid parseId,
        IDocumentParseService parseService,
        IDocumentParseBlockRepository blockRepository,
        IOssService ossService,
        ILoggerFactory loggerFactory,
        [FromQuery] int? afterSequence = null,
        [FromQuery] int? limit = null)
    {
        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null) return StructureHttpResults.ParseNotFound();

        return Results.Ok(new
        {
            success = true,
            data = await ReadPageAsync(parse, parseService, blockRepository, ossService, loggerFactory,
                afterSequence, limit, fromSequence: null, toSequence: null),
        });
    }

    private static async Task<IResult> GetNodeBlocks(
        Guid parseId,
        Guid nodeId,
        IDocumentParseService parseService,
        IDocumentParseBlockRepository blockRepository,
        IDocumentStructureRepository structureRepository,
        IOssService ossService,
        ILoggerFactory loggerFactory,
        [FromQuery] int? afterSequence = null,
        [FromQuery] int? limit = null)
    {
        var parse = await parseService.GetByIdAsync(parseId);
        if (parse == null) return StructureHttpResults.ParseNotFound();

        var node = (await structureRepository.GetByParseIdAsync(parseId)).FirstOrDefault(n => n.Id == nodeId);
        if (node == null)
            return Results.NotFound(new { success = false, message = "Structure node not found", errorCode = StructureErrorCodes.NodeNotFound });

        return Results.Ok(new
        {
            success = true,
            data = await ReadPageAsync(parse, parseService, blockRepository, ossService, loggerFactory,
                afterSequence, limit, node.StartSequence, node.EndSequence),
        });
    }

    private static async Task<IResult> GetStructure(Guid parseId, IDocumentStructureService structureService)
    {
        var tree = await structureService.GetTreeAsync(parseId);
        return tree == null
            ? StructureHttpResults.ParseNotFound()
            : Results.Ok(new { success = true, data = StructureHttpResults.Tree(tree) });
    }

    private static async Task<IResult> SaveStructure(
        Guid parseId,
        HttpRequest request,
        IDocumentStructureService structureService,
        ParseSearchIndexer searchIndexer,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(nameof(DocumentStructureEndpoints));
        SaveStructureRequest? body;
        try
        {
            body = await request.ReadFromJsonAsync<SaveStructureRequest>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            logger.LogWarning("Rejected an unreadable structure tree request body for parse {ParseId}", parseId);
            return StructureHttpResults.InvalidBody();
        }

        if (body?.Nodes == null) return StructureHttpResults.Invalid([new StructureFieldError("nodes", "is required")]);

        var result = await structureService.SaveTreeAsync(parseId, body.Nodes.Select(ToInput).ToList());
        if (result.Status != StructureResultStatus.Ok)
            return StructureHttpResults.From(result, StructureHttpResults.Tree);

        // Best-effort: a failed index write is logged and reported, never fails the save.
        var indexed = await searchIndexer.ReindexAsync(parseId);
        if (!indexed)
            logger.LogWarning("Structure of parse {ParseId} saved but the search index was not updated", parseId);

        return Results.Ok(new
        {
            success = true,
            data = StructureHttpResults.Tree(result.Value!),
            searchIndexUpdated = indexed,
        });
    }

    private static async Task<IResult> ReindexParse(Guid parseId, IDocumentParseService parseService, ParseSearchIndexer searchIndexer)
    {
        if (await parseService.GetByIdAsync(parseId) == null) return StructureHttpResults.ParseNotFound();

        return await searchIndexer.ReindexAsync(parseId)
            ? Results.Ok(new { success = true, data = new { parseId = parseId.ToString(), indexed = true } })
            : Results.Json(new { success = false, message = "Search index update failed", errorCode = "DOCTHECA_SEARCH_INDEX_FAILED" },
                statusCode: StatusCodes.Status502BadGateway);
    }

    private static async Task<IResult> ReindexAll(ParseSearchIndexer searchIndexer, CancellationToken cancellationToken)
    {
        var (total, indexed) = await searchIndexer.ReindexAllAsync(cancellationToken);
        return Results.Ok(new { success = true, data = new { total, indexed, failed = total - indexed } });
    }

    private static StructureNodeInput ToInput(NodeRequest? node) => node is null
        ? null!
        : new StructureNodeInput(node.Id, node.LevelId, node.Name, node.StartSequence, node.EndSequence,
            node.Children?.Select(ToInput).ToList());

    private static async Task<object> ReadPageAsync(
        DocumentParseModel parse,
        IDocumentParseService parseService,
        IDocumentParseBlockRepository blockRepository,
        IOssService ossService,
        ILoggerFactory loggerFactory,
        int? afterSequence,
        int? limit,
        int? fromSequence,
        int? toSequence)
    {
        var take = Math.Clamp(limit ?? DefaultBlockLimit, 1, MaxBlockLimit);
        // One extra row tells whether another page exists without a count query.
        var blocks = await blockRepository.GetPageBySequenceAsync(parse.Id, afterSequence, take + 1, fromSequence, toSequence);
        var hasMore = blocks.Count > take;
        if (hasMore) blocks.RemoveAt(blocks.Count - 1);

        var imageUrls = new Dictionary<Guid, string?>();
        if (blocks.Any(block => block.ImageId != null))
        {
            var resolver = ParseImageUrls.BuildResolver(parse, ossService, loggerFactory.CreateLogger(nameof(DocumentStructureEndpoints)));
            var wanted = blocks.Where(block => block.ImageId != null).Select(block => block.ImageId!.Value).ToHashSet();
            foreach (var image in (await parseService.GetImagesByParseIdAsync(parse.Id)).Where(image => wanted.Contains(image.Id)))
            {
                imageUrls[image.Id] = await resolver(image);
            }
        }

        return new
        {
            items = blocks.Select(block => new
            {
                id = block.Id.ToString(),
                sequence = block.Sequence,
                // page_id is 0-based; the reading view shows 1-based page numbers.
                pageNumber = block.PageId + 1,
                blockType = block.BlockType,
                subType = block.SubType,
                content = block.TextContent,
                contentFormat = string.IsNullOrEmpty(block.TextFormat) ? null : block.TextFormat,
                imageId = block.ImageId?.ToString(),
                imageUrl = block.ImageId is { } imageId ? imageUrls.GetValueOrDefault(imageId) : null,
            }),
            nextAfterSequence = hasMore ? blocks[^1].Sequence : null,
            hasMore,
        };
    }
}
