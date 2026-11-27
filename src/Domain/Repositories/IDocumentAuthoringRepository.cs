using Doctheca.Domain.Models;
using Doctheca.Domain.Services.Structure;

namespace Doctheca.Domain.Repositories;

public sealed record DocumentMetadataInput(string? FileName, string? Subject, string? Grade, string? Year,
    bool SetDefinition = false, Guid? StructureDefinitionId = null);

/// <summary>Document save order is the nested array order; bindings are optional and explicit.</summary>
public sealed record DocumentNodeInput(Guid? Id, Guid? LevelId, string? Name, Guid? ParseId,
    int? StartSequence, int? EndSequence, IReadOnlyList<DocumentNodeInput>? Children);
public sealed record DocumentTreeSave(DocumentStructureTree Tree, IReadOnlyCollection<Guid> AffectedParseIds);

public interface IDocumentAuthoringRepository
{
    Task<StructureResult<DocumentFile>> CreateAsync(DocumentMetadataInput input, Guid? createdBy, CancellationToken ct);
    Task<StructureResult<DocumentFile>> UpdateAsync(Guid id, DocumentMetadataInput input, CancellationToken ct);
    Task<DocumentFile?> FillMissingAsync(Guid id, string? subject, string? grade, string? year, CancellationToken ct);
    Task<StructureResult<DocumentFile>> AttachSourceAsync(Guid id, Guid sourceId, string mediaType, CancellationToken ct);
    Task<bool> IsSourceReferencedAsync(Guid documentId, Guid sourceId, CancellationToken ct);
    Task<DocumentStructureTree?> GetTreeAsync(Guid documentId, CancellationToken ct);
    Task<StructureResult<DocumentTreeSave>> SaveTreeAsync(Guid documentId, IReadOnlyList<DocumentNodeInput> nodes, CancellationToken ct);
}

public sealed class DocumentSourceRequiredException : InvalidOperationException
{
    public DocumentSourceRequiredException() : base("The document requires a source before parsing.") { }
}
