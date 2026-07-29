using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public sealed class IdentityTokenValidator : IIdentityTokenValidator
{
    private static readonly string[] UserIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];
    private static readonly string[] UsernameClaimTypes = ["unique_name", "name", ClaimTypes.Name];
    private static readonly string[] RoleClaimTypes = ["role", ClaimTypes.Role];

    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configurationManager;
    private readonly IdentityServiceOptions _options;

    public IdentityTokenValidator(
        IConfigurationManager<OpenIdConnectConfiguration> configurationManager,
        IOptions<IdentityServiceOptions> options)
    {
        _configurationManager = configurationManager;
        _options = options.Value;
    }

    public async Task<ValidatedIdentityToken> ValidateTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationManager.GetConfigurationAsync(cancellationToken);
        var parameters = CreateValidationParameters(_options, configuration.SigningKeys);
        var handler = new JwtSecurityTokenHandler
        {
            MapInboundClaims = false
        };

        var principal = handler.ValidateToken(accessToken, parameters, out var validatedToken);
        if (validatedToken is not JwtSecurityToken jwt)
        {
            throw new SecurityTokenValidationException("Identity token type is invalid.");
        }

        var roles = principal.Claims
            .Where(claim => RoleClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ValidatedIdentityToken(
            principal,
            FindFirstValue(principal, UserIdClaimTypes),
            FindFirstValue(principal, UsernameClaimTypes),
            roles,
            new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds(),
            roles.Contains("admin", StringComparer.OrdinalIgnoreCase));
    }

    public static TokenValidationParameters CreateValidationParameters(
        IdentityServiceOptions options,
        IEnumerable<SecurityKey>? signingKeys = null)
    {
        return new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = signingKeys,
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "unique_name",
            RoleClaimType = "role"
        };
    }

    private static string FindFirstValue(ClaimsPrincipal principal, IEnumerable<string> claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }
}
