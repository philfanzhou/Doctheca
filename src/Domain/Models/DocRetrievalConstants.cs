namespace Ruoyu.Study.DocLibrary.Domain.Models;

public static class DocLibraryConstants
{
    public const string SubjectEnglish = "英语";

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
