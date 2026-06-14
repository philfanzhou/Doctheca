namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Document parsing result
/// </summary>
public class ParsedDocument
{
    public List<ParsedPage> Pages { get; set; } = [];
}
