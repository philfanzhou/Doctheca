namespace Doctheca.Domain.Models;

// Explicit downstream projection; administrative/provider fields must stay out of this DTO.
public sealed record InternalSearchHit(string DocumentName, int PageNumber, string AssociatedText, double Score,
    string MatchType, string SegmentId, int StartOffset, int EndOffset, string? StructurePath);
public sealed record InternalSearchPage(IReadOnlyList<InternalSearchHit> Results, int TotalCount, string? NextPageToken);
