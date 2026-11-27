namespace Doctheca.Service.StructaDoc;

/// <summary>
/// StructaDoc Parse Run status values. Only the terminal statuses (succeeded, failed,
/// cancelled) end polling; every other status keeps the job in its polling cycle.
/// </summary>
public static class ParseRunStatus
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Running = "running";
    public const string RetryWait = "retry-wait";
    public const string CancelRequested = "cancel-requested";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
