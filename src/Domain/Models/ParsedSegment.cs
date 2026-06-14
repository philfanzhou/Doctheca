namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Parsed text segment
/// </summary>
public class ParsedSegment
{
    public string BlockId { get; set; } = string.Empty;
    public string SentenceId { get; set; } = string.Empty;
    public string SegmentType { get; set; } = SegmentTypes.Sentence;
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public List<ParsedToken> Tokens { get; set; } = [];
}
