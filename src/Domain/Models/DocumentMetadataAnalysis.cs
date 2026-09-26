namespace Doctheca.Domain.Models;

/// <summary>
/// LLM metadata analysis result. Fields are null if LLM could not determine them.
/// </summary>
public record DocumentMetadataAnalysis
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
