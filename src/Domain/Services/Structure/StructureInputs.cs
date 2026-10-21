namespace Doctheca.Domain.Services.Structure;

/// <summary>Create/replace payload of a structure definition; levels are listed in display order.</summary>
public sealed record StructureDefinitionInput(string? Name, IReadOnlyList<StructureLevelInput>? Levels);

/// <summary>
/// One level of a definition payload. <see cref="Id"/> is optional for a new level, but a level
/// referenced as <see cref="ParentLevelId"/> must carry an id in the same payload.
/// </summary>
public sealed record StructureLevelInput(Guid? Id, string? Name, Guid? ParentLevelId);

/// <summary>
/// One node of a tree save. Children are nested; sibling order follows <see cref="StartSequence"/>.
/// A node without <see cref="Id"/> gets a new id; an existing id is kept stable across saves.
/// </summary>
public sealed record StructureNodeInput(
    Guid? Id,
    Guid? LevelId,
    string? Name,
    int? StartSequence,
    int? EndSequence,
    IReadOnlyList<StructureNodeInput>? Children);
