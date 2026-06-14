namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Parsed token (original text + stemmed)
/// </summary>
public class ParsedToken
{
    public string TokenText { get; set; } = string.Empty;
    public string TokenStem { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}
