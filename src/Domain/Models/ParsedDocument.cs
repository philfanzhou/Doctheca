namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Document parsing result
/// </summary>
public class ParsedDocument
{
    public List<ParsedPage> Pages { get; set; } = [];

    /// <summary>
    /// LLM analysis profile (null if LLM not configured or analysis failed)
    /// </summary>
    public DocumentProfile? Profile { get; set; }
}
