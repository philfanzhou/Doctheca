namespace Doctheca.Domain.Models;

// Downstream allow-lists: never extend these with administrator or storage fields.
public sealed record InternalLevel(Guid Id, string Name, Guid? ParentLevelId, int SortOrder);
public sealed record InternalDefinition(Guid Id, string Name, IReadOnlyList<InternalLevel> Levels);
public sealed record InternalDocument(Guid Id, string Title, string? Subject, string? Grade,
    Guid? StructureDefinitionId, string? StructureDefinitionName, int AudioCount = 0);
public sealed record InternalCatalogQuery(int PageSize, Guid? After, Guid? StructureDefinitionId,
    string? Subject, string? Grade, bool HasAudio = false);
public sealed record InternalDocumentPage(bool DefinitionExists, IReadOnlyList<InternalDocument> Items);
