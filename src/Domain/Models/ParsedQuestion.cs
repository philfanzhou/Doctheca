namespace Ruoyu.Study.DocLibrary.Domain.Models;

/// <summary>
/// Parsed question
/// </summary>
public class ParsedQuestion
{
    public string QuestionId { get; set; } = string.Empty;
    public string Stem { get; set; } = string.Empty;
    public string? OptionsJson { get; set; }
    public string? AnswerArea { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public List<ParsedToken> Tokens { get; set; } = [];
}
