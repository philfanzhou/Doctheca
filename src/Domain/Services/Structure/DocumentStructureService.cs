using Microsoft.Extensions.Logging;
using Doctheca.Domain.Models;
using Doctheca.Domain.Models.Structure;
using Doctheca.Domain.Repositories;

namespace Doctheca.Domain.Services.Structure;

/// <summary>The marked structure of one parse together with the definition it was marked against.</summary>
public sealed class DocumentStructureTree
{
    public Guid? ParseId { get; init; }
    public Guid DocumentFileId { get; init; }
    public string FileName { get; init; } = string.Empty;
    public StructureDefinitionModel? Definition { get; init; }

    /// <summary>Flat nodes ordered parents-first by start sequence; rebuild the tree with <see cref="DocumentStructureNodeModel.ParentId"/>.</summary>
    public IReadOnlyList<DocumentStructureNodeModel> Nodes { get; init; } = [];
}

public interface IDocumentStructureService
{
    /// <summary>The parse's tree, or null when the parse does not exist.</summary>
    Task<DocumentStructureTree?> GetTreeAsync(Guid parseId);

    /// <summary>Validates and replaces the parse's whole tree; an invalid tree changes nothing.</summary>
    Task<StructureResult<DocumentStructureTree>> SaveTreeAsync(Guid parseId, IReadOnlyList<StructureNodeInput>? nodes);

    /// <summary>Chooses (or clears with null) the structure definition of a document file.</summary>
    Task<StructureResult<DocumentFileModel>> SetDocumentDefinitionAsync(Guid documentFileId, Guid? structureDefinitionId);
}

/// <summary>
/// Hand-marked document structure (issue #89). Nodes are inclusive ranges of block
/// <c>sequence</c> values. A save is validated as a whole: every start/end sequence exists in the
/// parse, start ≤ end, a root node uses a top level and a child node a level whose parent is its
/// parent node's level, all levels belong to the document's definition, children lie inside their
/// parent, and siblings do not overlap. Sibling order follows the start sequence.
/// </summary>
public sealed class DocumentStructureService : IDocumentStructureService
{
    internal const int MaxNodeNameLength = 500;
    internal const int MaxNodes = 5000;

    private readonly IDocumentParseRepository _parseRepository;
    private readonly IDocumentFileRepository _fileRepository;
    private readonly IDocumentParseBlockRepository _blockRepository;
    private readonly IDocumentStructureRepository _structureRepository;
    private readonly IStructureDefinitionRepository _definitionRepository;
    private readonly ILogger<DocumentStructureService> _logger;
    private readonly TimeProvider _timeProvider;

    public DocumentStructureService(
        IDocumentParseRepository parseRepository,
        IDocumentFileRepository fileRepository,
        IDocumentParseBlockRepository blockRepository,
        IDocumentStructureRepository structureRepository,
        IStructureDefinitionRepository definitionRepository,
        ILogger<DocumentStructureService> logger,
        TimeProvider? timeProvider = null)
    {
        _parseRepository = parseRepository;
        _fileRepository = fileRepository;
        _blockRepository = blockRepository;
        _structureRepository = structureRepository;
        _definitionRepository = definitionRepository;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DocumentStructureTree?> GetTreeAsync(Guid parseId)
    {
        var parse = await _parseRepository.GetByIdAsync(parseId);
        if (parse == null) return null;
        var file = await _fileRepository.GetByIdAsync(parse.DocumentFileId);
        var definition = file?.StructureDefinitionId is { } definitionId
            ? await _definitionRepository.GetByIdAsync(definitionId)
            : null;
        var nodes = OrderParentsFirst(await _structureRepository.GetByParseIdAsync(parseId));
        var bindings = nodes.Select(node => node.ParseId).Distinct().ToArray();
        return new DocumentStructureTree
        {
            ParseId = bindings.Length == 1 ? bindings[0] : null,
            DocumentFileId = parse.DocumentFileId,
            FileName = file?.FileName ?? string.Empty,
            Definition = definition,
            Nodes = nodes,
        };
    }

    public async Task<StructureResult<DocumentStructureTree>> SaveTreeAsync(
        Guid parseId, IReadOnlyList<StructureNodeInput>? nodes)
    {
        var parse = await _parseRepository.GetByIdAsync(parseId);
        if (parse == null)
            return StructureResult<DocumentStructureTree>.NotFound(StructureErrorCodes.ParseNotFound, "Parse record not found");

        if (parse.Status != DocumentParseStatus.Parsed)
            return StructureResult<DocumentStructureTree>.Invalid([new("parseId", "must reference a completed parse")]);

        var file = await _fileRepository.GetByIdAsync(parse.DocumentFileId);
        var definition = file?.StructureDefinitionId is { } definitionId
            ? await _definitionRepository.GetByIdAsync(definitionId)
            : null;
        var roots = nodes ?? [];

        if (roots.Count > 0 && definition == null)
        {
            return StructureResult<DocumentStructureTree>.Invalid(
                [new("nodes", "the document has no structure definition; choose one before marking nodes")]);
        }

        var context = new ValidationContext(
            definition?.Levels.ToDictionary(level => level.Id) ?? [],
            await _blockRepository.GetSequencesAsync(parseId),
            (await _structureRepository.GetByParseIdAsync(parseId)).ToDictionary(node => node.Id),
            _timeProvider.GetUtcNow());
        ValidateSiblings(context, roots, "nodes", parentNode: null, parentLevelId: null, parentId: null);

        if (context.Errors.Count == 0 && context.Flat.Count > MaxNodes)
            context.Errors.Add(new("nodes", $"at most {MaxNodes} nodes are allowed"));

        if (context.Errors.Count == 0)
        {
            var newIds = context.Flat.Select(node => node.Id).Where(id => !context.Existing.ContainsKey(id)).ToList();
            foreach (var foreign in await _structureRepository.GetForeignNodeIdsAsync(parseId, newIds))
            {
                context.Errors.Add(new(context.PathOf[foreign] + ".id", "belongs to another parse"));
            }
        }

        if (context.Errors.Count > 0)
            return StructureResult<DocumentStructureTree>.Invalid(context.Errors);

        if (!await _structureRepository.ReplaceAsync(parseId, context.Flat))
            return StructureResult<DocumentStructureTree>.NotFound(StructureErrorCodes.ParseNotFound, "Parse record not found");

        _logger.LogInformation("Structure tree saved for parse {ParseId}: {NodeCount} nodes", parseId, context.Flat.Count);
        return StructureResult<DocumentStructureTree>.Ok((await GetTreeAsync(parseId))!);
    }

    public async Task<StructureResult<DocumentFileModel>> SetDocumentDefinitionAsync(
        Guid documentFileId, Guid? structureDefinitionId)
    {
        var file = await _fileRepository.GetByIdAsync(documentFileId);
        if (file == null)
            return StructureResult<DocumentFileModel>.NotFound(StructureErrorCodes.FileNotFound, "File not found");

        if (file.StructureDefinitionId == structureDefinitionId)
            return StructureResult<DocumentFileModel>.Ok(file);

        if (structureDefinitionId is { } definitionId && await _definitionRepository.GetByIdAsync(definitionId) == null)
        {
            return StructureResult<DocumentFileModel>.Invalid(
                [new("structureDefinitionId", "structure definition does not exist")]);
        }

        // Saved trees reference the old definition's levels; they are cleared explicitly first.
        if (await _structureRepository.DocumentFileHasNodesAsync(documentFileId))
        {
            return StructureResult<DocumentFileModel>.Conflict(StructureErrorCodes.DocumentStructureInUse,
                "The document has marked structure nodes; clear them before changing its structure definition");
        }

        if (!await _fileRepository.SetStructureDefinitionAsync(documentFileId, structureDefinitionId))
            return StructureResult<DocumentFileModel>.Conflict(StructureErrorCodes.DocumentStructureInUse,
                "The document changed while choosing its structure definition; reload and retry");
        _logger.LogInformation("Document file {FileId} structure definition set to {DefinitionId}",
            documentFileId, structureDefinitionId?.ToString() ?? "(none)");
        return StructureResult<DocumentFileModel>.Ok((await _fileRepository.GetByIdAsync(documentFileId))!);
    }

    private sealed class ValidationContext(
        IReadOnlyDictionary<Guid, StructureLevelModel> levels,
        HashSet<int> sequences,
        IReadOnlyDictionary<Guid, DocumentStructureNodeModel> existing,
        DateTimeOffset now)
    {
        public IReadOnlyDictionary<Guid, StructureLevelModel> Levels { get; } = levels;
        public HashSet<int> Sequences { get; } = sequences;
        public IReadOnlyDictionary<Guid, DocumentStructureNodeModel> Existing { get; } = existing;
        public DateTimeOffset Now { get; } = now;
        public List<StructureFieldError> Errors { get; } = [];
        public List<DocumentStructureNodeModel> Flat { get; } = [];
        public Dictionary<Guid, string> PathOf { get; } = [];
    }

    private static void ValidateSiblings(ValidationContext context, IReadOnlyList<StructureNodeInput> siblings,
        string path, DocumentStructureNodeModel? parentNode, Guid? parentLevelId, Guid? parentId)
    {
        var accepted = new List<(DocumentStructureNodeModel Node, StructureNodeInput Input, string Path)>();
        for (var i = 0; i < siblings.Count; i++)
        {
            var input = siblings[i];
            var nodePath = $"{path}[{i}]";
            if (input == null)
            {
                context.Errors.Add(new(nodePath, "is required"));
                continue;
            }

            var node = ValidateNode(context, input, nodePath, parentNode, parentLevelId, parentId);
            if (node != null) accepted.Add((node, input, nodePath));
        }

        // Siblings are ordered by start sequence and must not overlap.
        accepted.Sort((left, right) => Nullable.Compare(left.Node.StartSequence, right.Node.StartSequence));
        for (var i = 1; i < accepted.Count; i++)
        {
            var previous = accepted[i - 1].Node;
            var current = accepted[i].Node;
            if (current.StartSequence <= previous.EndSequence)
            {
                context.Errors.Add(new(accepted[i].Path + ".startSequence",
                    $"overlaps sibling '{previous.Name}' ({previous.StartSequence}-{previous.EndSequence})"));
            }
        }

        for (var i = 0; i < accepted.Count; i++)
        {
            var (node, input, nodePath) = accepted[i];
            node.SortOrder = i;
            context.Flat.Add(node);
            ValidateSiblings(context, input.Children ?? [], nodePath + ".children", node, node.LevelId, node.Id);
        }
    }

    private static DocumentStructureNodeModel? ValidateNode(ValidationContext context, StructureNodeInput input,
        string path, DocumentStructureNodeModel? parentNode, Guid? parentLevelId, Guid? parentId)
    {
        var errorCount = context.Errors.Count;

        var id = input.Id ?? Guid.NewGuid();
        if (input.Id is { } given && !context.PathOf.TryAdd(given, path))
            context.Errors.Add(new($"{path}.id", "is used by more than one node"));
        else
            context.PathOf.TryAdd(id, path);

        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            context.Errors.Add(new($"{path}.name", "is required"));
        else if (name.Length > MaxNodeNameLength)
            context.Errors.Add(new($"{path}.name", $"must be at most {MaxNodeNameLength} characters"));

        if (input.LevelId is not { } levelId)
        {
            context.Errors.Add(new($"{path}.levelId", "is required"));
        }
        else if (!context.Levels.TryGetValue(levelId, out var level))
        {
            context.Errors.Add(new($"{path}.levelId", "does not belong to the document's structure definition"));
        }
        else if (level.ParentLevelId != parentLevelId)
        {
            context.Errors.Add(new($"{path}.levelId", parentLevelId == null
                ? "a root node must use a top level"
                : "must be a child level of the parent node's level"));
        }

        if (input.StartSequence is not { } start)
            context.Errors.Add(new($"{path}.startSequence", "is required"));
        else if (!context.Sequences.Contains(start))
            context.Errors.Add(new($"{path}.startSequence", $"sequence {start} does not exist in this parse"));

        if (input.EndSequence is not { } end)
            context.Errors.Add(new($"{path}.endSequence", "is required"));
        else if (!context.Sequences.Contains(end))
            context.Errors.Add(new($"{path}.endSequence", $"sequence {end} does not exist in this parse"));

        if (input.StartSequence is { } s && input.EndSequence is { } e)
        {
            if (s > e)
                context.Errors.Add(new($"{path}.startSequence", "must not be greater than endSequence"));
            else if (parentNode != null && (s < parentNode.StartSequence || e > parentNode.EndSequence))
                context.Errors.Add(new($"{path}.startSequence",
                    $"must lie inside the parent node's range ({parentNode.StartSequence}-{parentNode.EndSequence})"));
        }

        if (context.Errors.Count > errorCount) return null;

        var createdAt = context.Existing.TryGetValue(id, out var previous) ? previous.CreatedAt : context.Now;
        return new DocumentStructureNodeModel
        {
            Id = id,
            LevelId = input.LevelId!.Value,
            ParentId = parentId,
            Name = name!,
            StartSequence = input.StartSequence!.Value,
            EndSequence = input.EndSequence!.Value,
            CreatedAt = createdAt,
            UpdatedAt = context.Now,
        };
    }

    /// <summary>Orders stored nodes so each parent precedes its children, siblings by sort order.</summary>
    internal static List<DocumentStructureNodeModel> OrderParentsFirst(IReadOnlyCollection<DocumentStructureNodeModel> nodes)
    {
        var children = nodes.ToLookup(node => node.ParentId);
        var ordered = new List<DocumentStructureNodeModel>(nodes.Count);
        void Visit(Guid? parentId)
        {
            foreach (var node in children[parentId].OrderBy(node => node.SortOrder).ThenBy(node => node.StartSequence))
            {
                ordered.Add(node);
                Visit(node.Id);
            }
        }
        Visit(null);
        return ordered;
    }
}
