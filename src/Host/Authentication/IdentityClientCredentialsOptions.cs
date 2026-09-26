namespace Doctheca.Host.Authentication;

/// <summary>
/// Credentials for the Doctheca application registration in SignaCore.
/// They are deployment secrets and are intentionally separate from the shared JWT trust settings.
/// </summary>
public sealed class IdentityClientCredentialsOptions
{
    public const string SectionName = "IdentityService";

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;
}
