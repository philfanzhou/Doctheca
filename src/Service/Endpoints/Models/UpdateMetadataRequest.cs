namespace Doctheca.Service.Endpoints.Models;

/// <summary>
/// Request body for PUT /admin/document-files/{id}/metadata.
/// All fields optional — null means "no change", empty string means "clear".
/// </summary>
public record UpdateMetadataRequest
{
    public string? Subject { get; init; }
    public string? Grade { get; init; }
    public string? Year { get; init; }
}
