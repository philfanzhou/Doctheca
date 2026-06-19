using Ruoyu.Study.DocRetrieval.Domain.Models;

namespace Ruoyu.Study.DocRetrieval.Domain.Repositories;

/// <summary>
/// LLM-based intelligent document segmentation service
/// </summary>
public interface ILlmSegmentationService
{
    /// <summary>
    /// Analyze document to determine subject, type, and segmentation strategy.
    /// Uses the first portion of text as preview.
    /// </summary>
    /// <param name="textPreview">First ~2000 characters of the document</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Document profile with subject, type, and strategy</returns>
    Task<DocumentProfile> AnalyzeDocumentAsync(string textPreview, CancellationToken cancellationToken = default);

    /// <summary>
    /// Segment text into semantic units based on document profile.
    /// </summary>
    /// <param name="text">Text chunk to segment</param>
    /// <param name="profile">Document profile from AnalyzeDocumentAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of segment results</returns>
    Task<List<SegmentResult>> SegmentTextAsync(string text, DocumentProfile profile, CancellationToken cancellationToken = default);
}
