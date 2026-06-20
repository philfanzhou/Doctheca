namespace Ruoyu.Study.DocRetrieval.Domain.Models;

/// <summary>
/// Document profile produced by LLM analysis (subject, type, segmentation strategy)
/// </summary>
public record DocumentProfile
{
    /// <summary>
    /// Subject: English, 语文, 数学, 物理, 化学, 生物, 其他
    /// </summary>
    public string Subject { get; init; } = "其他";

    /// <summary>
    /// Grade: K, G1-G12, or empty if undetermined
    /// </summary>
    public string Grade { get; init; } = string.Empty;

    /// <summary>
    /// Document type: 教材, 知识点过关单, 单词表, 短语表, 试卷, 其他
    /// </summary>
    public string DocType { get; init; } = "其他";

    /// <summary>
    /// Segmentation strategy: sentence, concept, word_entry, question, knowledge_point
    /// </summary>
    public string SegmentStrategy { get; init; } = SegmentTypes.Sentence;

    /// <summary>
    /// Structural features of the document
    /// </summary>
    public DocumentStructure Structure { get; init; } = new();
}

/// <summary>
/// Structural features detected in the document
/// </summary>
public record DocumentStructure
{
    public bool HasChapters { get; init; }
    public bool HasQuestions { get; init; }
    public bool HasWordList { get; init; }
    public bool HasFormulas { get; init; }
}
