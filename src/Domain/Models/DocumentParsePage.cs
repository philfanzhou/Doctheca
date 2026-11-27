namespace Doctheca.Domain.Models;

/// <summary>Parse-owned physical page identity; printed labels are never keys.</summary>
public readonly record struct DocumentParsePageKey(Guid ParseId, int PageNumber);

public sealed record DocumentParsePage(Guid ParseId, int PageNumber, string? Label, string? LabelSource);
