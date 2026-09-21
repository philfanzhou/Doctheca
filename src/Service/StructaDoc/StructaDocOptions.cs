namespace Ruoyu.Study.DocLibrary.Service.StructaDoc;

/// <summary>
/// Configuration for the StructaDoc document parsing service integration (ADR-0009).
/// </summary>
public class StructaDocOptions
{
    public const string SectionName = "StructaDoc";

    /// <summary>Base URL of the StructaDoc instance, e.g. http://structadoc:8080.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>API key credential (sd1.&lt;clientId&gt;.&lt;secret&gt;) with documents/parses scopes.</summary>
    public string? ApiKey { get; set; }

    /// <summary>HTTP timeout in seconds for StructaDoc requests (uploads may be large).</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Optional mapping from DocLibrary model version ("vlm"/"pipeline") to a StructaDoc
    /// Provider Config ID. When a model is absent here, the enabled default Provider is used.
    /// </summary>
    public Dictionary<string, Guid> ProviderConfigIdByModel { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    public Guid? GetProviderConfigId(string modelVersion) =>
        ProviderConfigIdByModel.TryGetValue(modelVersion, out var id) ? id : null;
}
