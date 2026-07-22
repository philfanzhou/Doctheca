namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// Configuration options for MinerU Precision API.
/// </summary>
public class MinerUOptions
{
    public const string SectionName = "MinerU";

    /// <summary>Bearer Token for Precision Extract API.</summary>
    public string? ApiToken { get; set; }

    /// <summary>Base URL for MinerU API.</summary>
    public string? BaseUrl { get; set; } = "https://mineru.net";

    /// <summary>Model version: "vlm" (default) or "pipeline". Both modes may produce layout.json/content_list_v2.json/model.json depending on MinerU API version.</summary>
    public string? ModelVersion { get; set; }
}
