namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public static class DocRetrievalConstants
{
    public const string SubjectEnglish = "English";

    public static readonly string[] ValidSubjects = [SubjectEnglish];

    public static readonly string[] ValidGrades =
    [
        "K",     // Kindergarten
        "G1", "G2", "G3", "G4", "G5", "G6",     // Primary school
        "G7", "G8", "G9",                         // Junior high school
        "G10", "G11", "G12"                       // Senior high school
    ];

    public static readonly Dictionary<string, string> GradeLabels = new()
    {
        ["K"] = "Kindergarten",
        ["G1"] = "Grade 1", ["G2"] = "Grade 2", ["G3"] = "Grade 3",
        ["G4"] = "Grade 4", ["G5"] = "Grade 5", ["G6"] = "Grade 6",
        ["G7"] = "Grade 7", ["G8"] = "Grade 8", ["G9"] = "Grade 9",
        ["G10"] = "Grade 10", ["G11"] = "Grade 11", ["G12"] = "Grade 12"
    };

    public static bool IsValidSubject(string subject) => ValidSubjects.Contains(subject);
    public static bool IsValidGrade(string grade) => ValidGrades.Contains(grade);
}

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

/// <summary>
/// Segment type constants
/// </summary>
public static class SegmentTypes
{
    public const string Sentence = "sentence";
    public const string Question = "question";
}

/// <summary>
/// Source type constants
/// </summary>
public static class SourceTypes
{
    public const string Pdf = "pdf";
    public const string Word = "word";
    public const string Ppt = "ppt";
    public const string Unknown = "unknown";
}

/// <summary>
/// Search match type constants
/// </summary>
public static class SearchMatchType
{
    public const string ExactPhrase = "exact_phrase";
    public const string Stemmed = "stemmed";
    public const string ExactWord = "exact_word";
}
