namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public class OpenSearchOptions
{
    public string Url { get; set; } = "http://localhost:9200";
    public string IndexName { get; set; } = "docretrieval-segments";
}

public class QdrantOptions
{
    public string Url { get; set; } = "http://localhost:6333";
    public string CollectionName { get; set; } = "docretrieval_segments";
}

public class EmbeddingOptions
{
    public string ApiUrl { get; set; } = "https://api.siliconflow.cn/v1/embeddings";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "BAAI/bge-large-en-v1.5";
}

public class SearchFilterModel
{
    public string? DocumentTitle { get; set; }
    public string? Subject { get; set; }
    public string? Grade { get; set; }
    public string? Year { get; set; }
}

public class SearchResultModel
{
    public string DocumentName { get; set; } = string.Empty;
    public int PageNumber { get; set; }
    public string AssociatedText { get; set; } = string.Empty;
    public double Score { get; set; }
    public string MatchType { get; set; } = string.Empty;
    public string SegmentId { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}
