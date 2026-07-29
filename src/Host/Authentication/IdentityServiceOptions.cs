namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public sealed class IdentityServiceOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = string.Empty;
    public string Issuer { get; set; } = "QuantumZhou.Identity";
    public string Audience { get; set; } = "QuantumZhou.microservices";
    public bool RequireHttpsMetadata { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
}
