using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Services.Structure;

/// <summary>
/// Resolves a block sequence to the deepest marked node that contains it, with that node's
/// ancestors and the readable path of node names joined by <see cref="PathSeparator"/>, root first.
/// </summary>
public sealed class StructurePathResolver
{
    public const string PathSeparator = " > ";

    private readonly IReadOnlyList<DocumentStructureNodeModel> _nodes;
    private readonly IReadOnlyDictionary<Guid, DocumentStructureNodeModel> _byId;

    public StructurePathResolver(IReadOnlyCollection<DocumentStructureNodeModel> nodes)
    {
        _byId = nodes.ToDictionary(node => node.Id);
        // Deeper nodes are narrower: check the narrowest ranges first.
        _nodes = nodes
            .OrderBy(node => node.EndSequence - node.StartSequence)
            .ThenByDescending(node => Depth(node))
            .ToList();
    }

    public bool IsEmpty => _nodes.Count == 0;

    /// <summary>The block's position, or null when no node contains <paramref name="sequence"/>.</summary>
    public BlockStructurePosition? Resolve(int? sequence)
    {
        if (sequence is not { } value) return null;
        var deepest = _nodes.FirstOrDefault(node => node.StartSequence <= value && value <= node.EndSequence);
        if (deepest == null) return null;

        var chain = new List<DocumentStructureNodeModel>();
        for (var node = deepest; node != null; node = node.ParentId is { } parent && _byId.TryGetValue(parent, out var p) ? p : null)
        {
            chain.Add(node);
        }
        chain.Reverse();
        return new BlockStructurePosition(
            chain.Select(node => node.Id).ToList(),
            string.Join(PathSeparator, chain.Select(node => node.Name)));
    }

    private int Depth(DocumentStructureNodeModel node)
    {
        var depth = 0;
        for (var current = node; current.ParentId is { } parent && _byId.TryGetValue(parent, out var p); current = p)
        {
            depth++;
        }
        return depth;
    }
}
