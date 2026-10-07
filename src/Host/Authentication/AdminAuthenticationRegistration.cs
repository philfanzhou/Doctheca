using System.Security.Claims;
using System.Text.Encodings.Web;
using Doctheca.Common.Authentication;
using Doctheca.Service;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;

namespace Doctheca.Host.Authentication;

internal static class AdminAuthenticationRegistration
{
    internal static IServiceCollection AddDocthecaAdminAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var required = new[] { "IdentityService:Authority", "IdentityService:Issuer", "IdentityService:Audience" };
        if (required.Any(key => string.IsNullOrWhiteSpace(configuration[key])))
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddScheme<AuthenticationSchemeOptions, UnconfiguredBearerHandler>(JwtBearerDefaults.AuthenticationScheme, _ => { });
        }
        else
        {
            if (configuration["IdentityService:ClockSkewSeconds"] is { } skew && !int.TryParse(skew, out _))
                throw new InvalidOperationException("IdentityService:ClockSkewSeconds");
            services.AddRuoyuJwtBearer(configuration, environment, consumer =>
            {
                consumer.MapInboundClaims = false;
                consumer.NameClaimType = "unique_name";
                consumer.RoleClaimType = "role";
            });
        }
        services.AddAuthorization(options =>
        {
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            options.AddPolicy(DocthecaAuthorizationPolicies.Admin, policy =>
            {
                // The role assertion itself is unchanged; the session scheme of the policy is the
                // package's forwarding scheme, so a verified Bearer header is served by the host's
                // JwtBearer handler and everything else by the package's session (issue #70).
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                if (AdminOidcSettings.Read(configuration, environment).Available)
                    policy.AddAuthenticationSchemes(SignaCoreHostedLoginDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => context.User.Identities.Any(identity => identity.IsAuthenticated)
                    && context.User.Claims.Any(claim => claim.Type is "role" or ClaimTypes.Role
                    && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)));
            });
        });
        return services;
    }
}

// Keep policy/scheme names stable while refusing every credential without a trust anchor.
internal sealed class UnconfiguredBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
