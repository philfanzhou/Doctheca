namespace Doctheca.Common.Authentication;

/// <summary>
/// Defines the issuer trust contract for the host's JWT Bearer scheme.
/// Application credentials are intentionally not part of this contract.
/// </summary>
public sealed class IdentityOptions
{
    public const string SectionName = "IdentityService";

    public string Authority { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string[] AdditionalValidIssuers { get; set; } = [];
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 30;

    public IReadOnlyList<string> GetValidIssuers()
    {
        return new[] { Issuer }
            .Concat(AdditionalValidIssuers ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
