namespace Ruoyu.Study.DocLibrary.Service.StructaDoc;

/// <summary>
/// HTTP client for the StructaDoc public API v1 (API-key machine client).
/// Contract reference: StructaDoc GET /api/v1/openapi.json and docs/development/api-description.md.
/// </summary>
public interface IStructaDocClient
{
    /// <summary>Upload a document (multipart field "file"). Returns the created document.</summary>
    Task<StructaDocDocumentResponse> UploadDocumentAsync(
        string fileName, string contentType, Stream content, CancellationToken ct = default);

    /// <summary>
    /// Create a Parse Run for a document. The idempotency key makes retries after crashes safe;
    /// a replayed submission returns the original run.
    /// </summary>
    Task<StructaDocParseRunResponse> CreateParseRunAsync(
        Guid documentId, string idempotencyKey, Guid? providerConfigId = null, CancellationToken ct = default);

    /// <summary>Get a Parse Run; null when it does not exist or is not accessible.</summary>
    Task<StructaDocParseRunResponse?> GetParseRunAsync(Guid parseRunId, CancellationToken ct = default);

    /// <summary>Fetch all blocks of a succeeded Parse Run, following the sequence cursor.</summary>
    Task<List<StructaDocBlockResponse>> GetAllBlocksAsync(Guid parseRunId, CancellationToken ct = default);

    /// <summary>List assets (extracted images and binary resources) of a Parse Run.</summary>
    Task<List<StructaDocAssetResponse>> GetAssetsAsync(Guid parseRunId, CancellationToken ct = default);

    /// <summary>Download the canonical Markdown artifact of a Parse Run.</summary>
    Task<string> GetMarkdownAsync(Guid parseRunId, CancellationToken ct = default);

    /// <summary>Open the content stream of one asset. Caller disposes the stream.</summary>
    Task<Stream> GetAssetContentAsync(Guid parseRunId, Guid assetId, CancellationToken ct = default);

    /// <summary>Request document deletion (202 accepted lifecycle); idempotent, 404 is success.</summary>
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>Best-effort cancellation of a non-terminal Parse Run.</summary>
    Task CancelParseRunAsync(Guid parseRunId, CancellationToken ct = default);

    /// <summary>Delete a terminal Parse Run and its results; idempotent, 404 is success.</summary>
    Task DeleteParseRunAsync(Guid parseRunId, CancellationToken ct = default);
}
