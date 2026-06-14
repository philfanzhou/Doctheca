namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Parsed page
/// </summary>
public class ParsedPage
{
    public int PageNumber { get; set; }
    public List<ParsedSegment> Segments { get; set; } = [];
    public List<ParsedQuestion> Questions { get; set; } = [];
}
