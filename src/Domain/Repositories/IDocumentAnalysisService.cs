using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

/// <summary>
/// LLM-based document metadata analysis service.
/// Uses OpenAI-compatible APIs to analyze document subject/grade/year metadata
/// when MinerU parse completes and metadata is missing.
/// </summary>
public interface IDocumentAnalysisService
{
    /// <summary>
    /// Maximum text chunk size in characters for LLM calls.
    /// Dynamically calculated from model context length at initialization.
    /// </summary>
    int ChunkSize { get; }

    /// <summary>
    /// Maximum number of concurrent LLM calls.
    /// Controls parallelism to balance speed vs. provider rate limits.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Initialize model parameters. Called at startup and lazily on first use.
    /// Parses ContextLength config, computes ChunkSize.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze document text preview to determine subject, grade, and year metadata.
    /// Focused prompt for metadata only.
    /// Returns null on failure (caller should treat as best-effort).
    /// </summary>
    /// <param name="textPreview">First ~2000 characters of human-readable document content (markdown)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Metadata analysis result, or null if LLM call failed</returns>
    Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);
}
