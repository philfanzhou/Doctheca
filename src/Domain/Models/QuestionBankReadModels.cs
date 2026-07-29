namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// Parsed document metadata exposed to QuestionBank.
/// </summary>
public record ImportableParseItem(
    Guid ParseId,
    Guid FileId,
    string FileName,
    string ModelVersion,
    DateTimeOffset? ParsedAt);

/// <summary>
/// Structured block item with optional image metadata.
/// </summary>
public record ParseBlockItem(
    Guid Id,
    Guid ParseId,
    int PageId,
    int SortIndex,
    string BlockType,
    string? TextContent,
    string? ImageName,
    string? ImagePath,
    string? ImageUrl,
    object? BlockData);

/// <summary>
/// Image binary blob with metadata, ready for streaming response.
/// </summary>
public record ParseImageBlob(
    string ImageName,
    string ContentType,
    Stream Stream);
