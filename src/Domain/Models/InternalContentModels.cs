namespace Doctheca.Domain.Models;

public sealed record InternalContentQuery(int? AfterSequence, int Limit, string? Revision);
public sealed record InternalContentItem(Guid Id, int Sequence, string BlockType, string? SubType,
    string? Content, string? ContentFormat, int PageNumber, Guid? ImageId, string? ImageUrl);
public sealed record InternalContentPage(Guid? ParseId, Guid NodeId, string Revision,
    IReadOnlyList<InternalContentItem> Items, int? NextAfterSequence, bool HasMore);
public enum InternalContentStatus { Found, NotFound, InvalidCursor, Changed }
public sealed record InternalContentRead(InternalContentStatus Status, InternalContentPage? Page = null);

// Server-side source references. These records must never be serialized into an HTTP response.
public sealed record InternalImageRead(InternalContentStatus Status, DocumentParseModel? Parse = null, DocumentParseImageModel? Image = null);
