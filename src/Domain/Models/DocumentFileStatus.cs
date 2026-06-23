namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public static class DocumentFileStatus
{
    public const string Uploaded = "uploaded";
    public const string PendingParse = "pending_parse";
    public const string Parsing = "parsing";
    public const string Parsed = "parsed";
    public const string ParseFailed = "parse_failed";
}
