using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Strict validator for the SignaCore ID token: exactly one of each required claim, RS256,
/// typ JWT, kid present, aud = client id, iss = authority, sane iat/exp — all checked on the
/// raw JSON before the base-class signature and lifetime verification runs.
/// </summary>
public sealed class StrictIdTokenHandler(AdminOidcSettings settings, TimeProvider time) : JsonWebTokenHandler
{
    public override async Task<TokenValidationResult> ValidateTokenAsync(string token, TokenValidationParameters parameters)
    {
        try
        {
            // This check only rejects malformed structure. No identity is trusted until base verifies the signature.
            var parts = token.Split('.');
            if (parts.Length != 3) return Rejected();
            using var header = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[0]));
            var headers = header.RootElement.EnumerateObject().ToArray();
            if (headers.GroupBy(p => p.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
                || !StringClaim(headers, "kid", out var kid) || string.IsNullOrWhiteSpace(kid)
                || !StringClaim(headers, "alg", out var alg) || alg != "RS256"
                || !StringClaim(headers, "typ", out var typ) || typ != "JWT") return Rejected();
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            var claims = payload.RootElement.EnumerateObject().ToArray();
            if (claims.GroupBy(p => p.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)) return Rejected();
            if (!StringClaim(claims, "sub", out var sub) || string.IsNullOrWhiteSpace(sub)
                || !StringClaim(claims, "aud", out var aud) || aud != settings.ClientId
                || !StringClaim(claims, "iss", out var iss) || iss != settings.Authority
                || !Epoch(claims, "iat", out var iat) || !Epoch(claims, "exp", out var exp)
                || exp <= iat || iat > time.GetUtcNow() + settings.ClockSkew) return Rejected();
            var strict = parameters.Clone();
            strict.RequireSignedTokens = true;
            strict.TryAllIssuerSigningKeys = false;
            strict.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            strict.ValidTypes = ["JWT"];
            strict.ValidIssuer = settings.Authority;
            strict.ValidIssuers = null;
            strict.ValidAudience = settings.ClientId;
            strict.ValidAudiences = null;
            strict.IssuerValidator = (issuer, _, _) => issuer == settings.Authority
                ? issuer : throw new SecurityTokenInvalidIssuerException("oidc.invalid_id_token");
            strict.ValidateLifetime = true;
            strict.RequireExpirationTime = true;
            strict.LifetimeValidator = (notBefore, expires, _, _) => expires is { } end
                && new DateTimeOffset(end, TimeSpan.Zero) > time.GetUtcNow() - settings.ClockSkew
                && (notBefore is null || new DateTimeOffset(notBefore.Value, TimeSpan.Zero) <= time.GetUtcNow() + settings.ClockSkew);
            return await base.ValidateTokenAsync(token, strict);
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException or InvalidOperationException)
        {
            return Rejected();
        }
    }

    private static bool StringClaim(JsonProperty[] claims, string name, out string? value)
    {
        var matches = claims.Where(p => p.Name == name).ToArray();
        value = matches.Length == 1 && matches[0].Value.ValueKind == JsonValueKind.String ? matches[0].Value.GetString() : null;
        return value is not null;
    }

    private static bool Epoch(JsonProperty[] claims, string name, out DateTimeOffset value)
    {
        var matches = claims.Where(p => p.Name == name).ToArray();
        value = default;
        if (matches.Length != 1 || !matches[0].Value.TryGetInt64(out var seconds) || seconds < 0) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static TokenValidationResult Rejected() => new()
    {
        IsValid = false,
        Exception = new SecurityTokenValidationException("oidc.invalid_id_token")
    };
}
