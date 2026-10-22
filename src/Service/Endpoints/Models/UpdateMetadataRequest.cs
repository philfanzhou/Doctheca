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

    /// <summary>
    /// The structure definition the document uses (issue #89): a definition id sets it, an empty
    /// string clears it, null leaves it unchanged. Rejected with 409 while any parse of the
    /// document has marked structure nodes.
    /// </summary>
    public string? StructureDefinitionId { get; init; }
}
