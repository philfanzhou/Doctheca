namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Progress report from document parsing, used by IngestionWorker
/// to calculate overall ingestion progress.
/// </summary>
public record ParsingProgress
{
    /// <summary>
    /// Stage name: "analyzing" (LLM document analysis) or "parsing" (LLM segmentation)
    /// </summary>
    public string Stage { get; init; } = string.Empty;

    /// <summary>
    /// Completed steps within this stage (0-based, incremented after each step completes)
    /// </summary>
    public int CompletedSteps { get; init; }

    /// <summary>
    /// Total steps in this stage
    /// </summary>
    public int TotalSteps { get; init; }
}
