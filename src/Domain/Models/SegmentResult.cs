namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// Single segment result returned by LLM segmentation
/// </summary>
public record SegmentResult
{
    /// <summary>
    /// Segment text content
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Start offset relative to the input text chunk
    /// </summary>
    public int StartOffset { get; init; }

    /// <summary>
    /// End offset relative to the input text chunk
    /// </summary>
    public int EndOffset { get; init; }

    /// <summary>
    /// Segment type: sentence, concept, word_entry, question, knowledge_point
    /// </summary>
    public string SegmentType { get; init; } = SegmentTypes.Sentence;
}
