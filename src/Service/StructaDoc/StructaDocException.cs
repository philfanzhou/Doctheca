namespace Ruoyu.Study.DocLibrary.Service.StructaDoc;

/// <summary>
/// Raised when a StructaDoc API call fails. <see cref="IsTransient"/> distinguishes
/// retryable failures (network, timeout, 5xx, 429) from permanent ones so callers can
/// decide between retry-later and fail-fast handling.
/// </summary>
public class StructaDocException : Exception
{
    public StructaDocException(string message, int statusCode, bool isTransient, string? problemTitle = null, string? problemCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
        ProblemTitle = problemTitle;
        ProblemCode = problemCode;
    }

    public StructaDocException(string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = 0;
        IsTransient = true;
    }

    /// <summary>HTTP status code, or 0 for transport-level failures.</summary>
    public int StatusCode { get; }

    /// <summary>True when retrying the same request later may succeed.</summary>
    public bool IsTransient { get; }

    /// <summary>RFC 7807 problem title, when the server returned problem+json.</summary>
    public string? ProblemTitle { get; }

    /// <summary>Machine-readable error code extension member, when present.</summary>
    public string? ProblemCode { get; }
}
