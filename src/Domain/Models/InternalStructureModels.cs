namespace Doctheca.Domain.Models;

// Downstream tree allow-list, independent of the administrative tree contract.
public sealed record InternalStructureTree(Guid DocumentFileId, Guid? ParseId, IReadOnlyList<InternalStructureNode> Nodes);
public sealed record InternalStructureNode(Guid Id, Guid LevelId, string LevelName, string Name, string Path,
    int? StartSequence, int? EndSequence, int? StartPageNumber, int? EndPageNumber, IReadOnlyList<InternalStructureNode> Children, int AudioCount = 0);

public enum InternalStructureReadStatus { Found, DocumentNotFound, StructureNotFound }
public sealed record InternalStructureRead(InternalStructureReadStatus Status, InternalStructureTree? Tree = null);

/// <summary>
/// A node resolved to its owning document's current structured parse in one read snapshot.
/// This is read evidence, not a lease: later readers must resolve ownership in their own snapshot.
/// </summary>
public sealed record InternalCurrentStructureNode(Guid DocumentFileId, Guid? ParseId, Guid NodeId, int? StartSequence, int? EndSequence, Guid TreeId = default);
