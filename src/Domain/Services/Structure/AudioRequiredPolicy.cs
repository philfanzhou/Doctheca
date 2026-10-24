using Doctheca.Domain.Models.Structure;

namespace Doctheca.Domain.Services.Structure;

/// <summary>Policy is evaluated from the definition graph, including levels with no current nodes.</summary>
public sealed class AudioRequiredPolicy
{
    private readonly IReadOnlyDictionary<Guid, DocumentStructureNodeModel> _nodes;
    private readonly Dictionary<Guid, StructureLevelModel> _levels;
    private readonly HashSet<Guid> _aboveRequired = [];
    private readonly bool _required;

    public AudioRequiredPolicy(IReadOnlyList<StructureLevelModel> levels, IReadOnlyDictionary<Guid, DocumentStructureNodeModel> nodes)
    {
        _nodes = nodes;
        _levels = levels.ToDictionary(level => level.Id);
        _required = levels.Any(level => level.Required);
        foreach (var required in levels.Where(level => level.Required))
        {
            var seen = new HashSet<Guid>();
            for (var parent = required.ParentLevelId; parent is { } p && seen.Add(p) && _levels.TryGetValue(p, out var ancestor); parent = ancestor.ParentLevelId)
                _aboveRequired.Add(p);
        }
    }

    public string? Reason(Guid? nodeId)
    {
        if (nodeId is not { } id || !_nodes.TryGetValue(id, out var node))
            return _required ? "must be linked to a node at or below the deepest required level" : null;
        if (!_levels.ContainsKey(node.LevelId)) return "the target level is no longer in the structure definition";
        return _aboveRequired.Contains(node.LevelId) ? "must be linked at or below the deepest required level on this branch" : null;
    }
}
