namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// All parsed artifacts from a single MinerU task ZIP.
/// </summary>
public record MinerUParseResult(
    byte[] ZipBytes,
    string Markdown,
    string ContentListJson,
    string? ContentListV2Json,
    string? ModelJson,
    string? LayoutJson,
    List<ImageMetadata> Images);
