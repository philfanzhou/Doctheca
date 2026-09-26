namespace Doctheca.Service.StructaDoc;

/// <summary>
/// StructaDoc Parse Run status values. Only <see cref="IsTerminal"/> statuses end polling.
/// </summary>
public static class StructaDocParseRunStatus
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Running = "running";
    public const string RetryWait = "retry-wait";
    public const string CancelRequested = "cancel-requested";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string? status) =>
        status is Succeeded or Failed or Cancelled;
}
