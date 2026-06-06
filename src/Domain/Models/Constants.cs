namespace Ruoyu.Study.DocRetrieval.Domain.Models;

public static class DocRetrievalConstants
{
    public const string SubjectEnglish = "英语";

    public static readonly string[] ValidSubjects = [SubjectEnglish];

    public static readonly string[] ValidGrades =
    [
        "K",     // 幼儿园
        "G1", "G2", "G3", "G4", "G5", "G6",     // 小学
        "G7", "G8", "G9",                         // 初中
        "G10", "G11", "G12"                       // 高中
    ];

    public static readonly Dictionary<string, string> GradeLabels = new()
    {
        ["K"] = "幼儿园",
        ["G1"] = "一年级", ["G2"] = "二年级", ["G3"] = "三年级",
        ["G4"] = "四年级", ["G5"] = "五年级", ["G6"] = "六年级",
        ["G7"] = "初一", ["G8"] = "初二", ["G9"] = "初三",
        ["G10"] = "高一", ["G11"] = "高二", ["G12"] = "高三"
    };

    public static bool IsValidSubject(string subject) => ValidSubjects.Contains(subject);
    public static bool IsValidGrade(string grade) => ValidGrades.Contains(grade);
}
