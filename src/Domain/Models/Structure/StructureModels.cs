namespace Doctheca.Domain.Models.Structure;

/// <summary>A runtime-configured kind of document structure and its levels.</summary>
public sealed class StructureDefinitionModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Levels in display order (<see cref="StructureLevelModel.SortOrder"/>).</summary>
    public List<StructureLevelModel> Levels { get; set; } = [];
}

/// <summary>One configured level; a null <see cref="ParentLevelId"/> marks a top level.</summary>
public sealed class StructureLevelModel
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid? ParentLevelId { get; set; }
    public int SortOrder { get; set; }
    public bool Required { get; set; }
}

/// <summary>
/// A hand-marked node of one parse: the inclusive block <c>sequence</c> range
/// [<see cref="StartSequence"/>, <see cref="EndSequence"/>] at one configured level.
/// </summary>
public sealed class DocumentStructureNodeModel
{
    public Guid Id { get; set; }
    public Guid DocumentFileId { get; set; }
    public Guid TreeId { get; set; }
    public Guid? ParseId { get; set; }
    public Guid LevelId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int? StartSequence { get; set; }
    public int? EndSequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The structure position of one block: its node and ancestors, and the readable path.</summary>
public sealed record BlockStructurePosition(IReadOnlyList<Guid> NodeIds, string Path);
