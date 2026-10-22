namespace Doctheca.Domain.Models;

/// <summary>A local block UUID and its indexed, zero-based physical page number.</summary>
public readonly record struct SearchBlockPageKey(Guid BlockId, int PageNumber);
