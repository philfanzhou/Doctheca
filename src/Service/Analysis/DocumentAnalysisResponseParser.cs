using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.DocLibrary.Domain.Models;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Parses LLM responses for document metadata analysis.
/// </summary>
internal static class DocumentAnalysisResponseParser
{
    /// <summary>
    /// Parse LLM metadata analysis response into DocumentMetadataAnalysis.
    /// Returns null on parse failure.
    /// </summary>
    internal static DocumentMetadataAnalysis? ParseMetadataAnalysis(
        string response,
        ILogger<DocumentAnalysisService> logger,
        JsonSerializerOptions jsonOptions)
    {
        try
        {
            var json = ExtractJson(response);
            var parsed = JsonSerializer.Deserialize<MetadataAnalysisResponse>(json, jsonOptions);

            if (parsed == null)
            {
                logger.LogWarning("Failed to parse LLM metadata analysis response");
                return null;
            }

            // Treat empty strings as null (LLM may return "" instead of null)
            var subject = string.IsNullOrWhiteSpace(parsed.Subject) ? null : parsed.Subject.Trim();
            var grade = string.IsNullOrWhiteSpace(parsed.Grade) ? null : parsed.Grade.Trim();
            var year = string.IsNullOrWhiteSpace(parsed.Year) ? null : parsed.Year.Trim();

            // Skip "null" string literal that some LLMs return instead of JSON null
            subject = subject == "null" ? null : subject;
            grade = grade == "null" ? null : grade;
            year = year == "null" ? null : year;

            return new DocumentMetadataAnalysis
            {
                Subject = subject,
                Grade = grade,
                Year = year
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM metadata analysis response as JSON");
            return null;
        }
    }

    /// <summary>
    /// Extract JSON from response, handling markdown code blocks.
    /// </summary>
    internal static string ExtractJson(string response)
    {
        var trimmed = response.Trim();

        // Remove markdown code block wrapper if present
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```");
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }
        else if (trimmed.StartsWith("```"))
        {
            var start = trimmed.IndexOf('\n');
            var end = trimmed.LastIndexOf("```");
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[(start + 1)..end].Trim();
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Model info returned by the OpenAI-compatible /models endpoint.
    /// </summary>
    internal record ModelInfoResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("context_length")]
        public int ContextLength { get; init; }
    }

    /// <summary>
    /// LLM response for metadata-only analysis (subject, grade, year).
    /// All fields nullable — LLM returns null when it cannot determine the value.
    /// </summary>
    internal record MetadataAnalysisResponse
    {
        [JsonPropertyName("subject")]
        public string? Subject { get; init; }

        [JsonPropertyName("grade")]
        public string? Grade { get; init; }

        [JsonPropertyName("year")]
        public string? Year { get; init; }
    }
}
