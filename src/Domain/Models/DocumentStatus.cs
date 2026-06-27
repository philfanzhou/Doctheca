namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// Document and ingestion job status constants
/// </summary>
public static class DocumentStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Ready = "ready";
    public const string Success = "success";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
