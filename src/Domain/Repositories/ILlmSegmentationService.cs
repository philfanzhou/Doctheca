using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Domain.Repositories;

/// <summary>
/// LLM-based intelligent document segmentation service
/// </summary>
public interface ILlmSegmentationService
{
    /// <summary>
    /// Maximum text chunk size in characters for LLM calls.
    /// Dynamically calculated from model context length at initialization.
    /// </summary>
    int ChunkSize { get; }

    /// <summary>
    /// Maximum number of concurrent LLM calls during chunk segmentation.
    /// Controls parallelism to balance speed vs. provider rate limits.
    /// </summary>
    int MaxConcurrency { get; }

    /// <summary>
    /// Initialize model parameters. Called at startup and lazily on first use.
    /// Parses ContextLength config, computes ChunkSize.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze document to determine subject, type, and segmentation strategy.
    /// Uses the first portion of text as preview.
    /// </summary>
    /// <param name="textPreview">First ~2000 characters of the document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Document profile with subject, type, and strategy</returns>
    Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyze document text preview to determine subject, grade, and year metadata.
    /// Focused prompt for metadata only (no segmentation strategy).
    /// Returns null on failure (caller should treat as best-effort).
    /// </summary>
    /// <param name="textPreview">First ~2000 characters of human-readable document content (markdown)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Metadata analysis result, or null if LLM call failed</returns>
    Task<DocumentMetadataAnalysis?> AnalyzeMetadataAsync(string textPreview, CancellationToken cancellationToken = default);

    /// <summary>
    /// Segment text into semantic units based on document profile.
    /// </summary>
    /// <param name="text">Text chunk to segment</param>
    /// <param name="profile">Document profile from AnalyzeDocumentAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of segment results</returns>
    Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-analyze document profile using user corrections as few-shot examples.
    /// </summary>
    Task<DocumentProfile> RefineProfileAsync(
        string textPreview,
        DocumentProfile originalProfile,
        List<SegmentCorrection> corrections,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-segment text using user corrections as few-shot examples.
    /// </summary>
    Task<List<SegmentResult>> RefineSegmentTextAsync(
        string text,
        DocumentProfile profile,
        List<SegmentCorrection> corrections,
        CancellationToken cancellationToken = default);
}
