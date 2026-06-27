namespace Ruoyu.Study.DocLibrary.Domain.Models;

public class OpenSearchOptions
{
    public string Url { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "docretrieval-segments";
}
