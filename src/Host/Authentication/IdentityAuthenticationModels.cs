using System.Security.Claims;
using System.Text.Json.Serialization;

namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public enum IdentityExchangeStatus
{
    Succeeded,
    Rejected,
    InvalidResponse,
    Unavailable
}

public sealed record IdentityTokenExchangeResult(
    IdentityExchangeStatus Status,
    string AccessToken = "",
    string RefreshToken = "",
    long ExpiresAt = 0,
    string? Message = null);

public sealed record ValidatedIdentityToken(
    ClaimsPrincipal Principal,
    string UserId,
    string Username,
    IReadOnlyList<string> Roles,
    long ExpiresAt,
    bool IsAdministrator);

internal sealed record IdentityTokenRequest(
    [property: JsonPropertyName("grantType")] string GrantType,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("password")] string? Password = null,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken = null);

internal sealed class IdentityTokenResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public long ExpiresAt { get; init; }
}

internal sealed record IdentityRevokeRequest(
    [property: JsonPropertyName("refreshToken")] string RefreshToken);
