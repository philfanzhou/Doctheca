using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Doctheca.Domain.Services;

namespace Doctheca.Service.Parsing;

/// <summary>
/// Re-indexes parses into OpenSearch with their current metadata and hand-marked structure.
/// Used after a structure save and by the administrator's repair endpoints. Failures are logged
/// as warnings and reported as false; they never fail the caller's database work.
/// </summary>
public sealed class ParseSearchIndexer
{
    private readonly IDocumentParseService _parseService;
    private readonly IDocumentFileService _fileService;
    private readonly ISearchIndexService _searchIndexService;
    private readonly ILogger<ParseSearchIndexer> _logger;

    public ParseSearchIndexer(
        IDocumentParseService parseService,
        IDocumentFileService fileService,
        ISearchIndexService searchIndexService,
        ILogger<ParseSearchIndexer> logger)
    {
        _parseService = parseService;
        _fileService = fileService;
        _searchIndexService = searchIndexService;
        _logger = logger;
    }

    /// <summary>Re-indexes one parse; false when the parse is missing or the write failed.</summary>
    public async Task<bool> ReindexAsync(Guid parseId)
    {
        var parse = await _parseService.GetByIdAsync(parseId);
        return parse != null && await ReindexAsync(parse);
    }

    /// <summary>Re-indexes every parsed parse sequentially and returns (total, indexed).</summary>
    public async Task<(int Total, int Indexed)> ReindexAllAsync(CancellationToken cancellationToken)
    {
        var parses = await _parseService.GetByStatusAsync(DocumentParseStatus.Parsed);
        var indexed = 0;
        foreach (var parse in parses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ReindexAsync(parse)) indexed++;
        }

        _logger.LogInformation("Search re-index finished: {Indexed}/{Total} parses indexed", indexed, parses.Count);
        return (parses.Count, indexed);
    }

    private async Task<bool> ReindexAsync(DocumentParseModel parse)
    {
        try
        {
            var file = await _fileService.GetByIdAsync(parse.DocumentFileId);
            if (file == null) return false;
            return await _searchIndexService.IndexParseBlocksAsync(
                parse.Id, file.Id, file.FileName, file.Subject, file.Grade, file.Year);
        }
        catch (Exception)
        {
            // Index provider errors can contain the document body or annotations.
            _logger.LogWarning("Failed to re-index parse {ParseId} in OpenSearch", parse.Id);
            return false;
        }
    }
}
