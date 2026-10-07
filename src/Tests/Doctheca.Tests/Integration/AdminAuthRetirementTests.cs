using System.Net;
using System.Text;
using Doctheca.Host.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminOidcIntegrationTests
{
    [Theory]
    [InlineData("login")][InlineData("refresh")][InlineData("logout")]
    public async Task RetiredEndpointsIgnoreEveryBodyAndCookieAndNeverCallProvider(string route)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var session = SessionCookie(await Callback(client, handshake, authority.Code(handshake.Query)));
        var redeems = authority.Redeems;
        foreach (var (body, type) in new[] { ("", "application/json"), ("{broken", "application/json"),
            ("{\"username\":\"synthetic\",\"password\":\"fictitious-password-canary\"}", "application/json"),
            ("username=synthetic&password=fictitious-password-canary", "text/plain") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/" + route)
            { Content = new StringContent(body, Encoding.UTF8, type) };
            request.Headers.Add("Cookie", session + "; docthecaAccessToken=" + CreateToken("admin") + "; docthecaRefreshToken=fictitious-refresh-canary");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("{\"success\":false,\"message\":\"Password authentication has been retired. Use hosted sign-in.\"}", await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(redeems, authority.Redeems);
        Assert.Empty(authority.LogoutForms);
        // The retired logout must not revoke the current opaque session.
        using var probe = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        probe.Headers.Add("Cookie", session + "; docthecaAccessToken=invalid; docthecaRefreshToken=invalid");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(probe)).StatusCode);
        Assert.Equal(1, Tickets(factory).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("IdentityService:Authority")][InlineData("IdentityService:AppId")]
    [InlineData("IdentityService:AppSecret")][InlineData("AdminOidc:RedirectUri")]
    [InlineData("AdminOidc:PostLogoutRedirectUri")]
    public async Task ProtocolQueriesNeverEnterTraceLogsWithCompleteOrMissingHostedConfiguration(string? missing)
    {
        using var authority = new OidcTestAuthority();
        var logs = new OidcLogCapture();
        var settings = new Dictionary<string, string?> {
            ["AdminOidc:RedirectUri"] = OidcTestAuthority.RedirectUri,
            ["AdminOidc:PostLogoutRedirectUri"] = OidcTestAuthority.PostLogoutUri,
            ["IdentityService:Authority"] = OidcTestAuthority.Issuer,
            ["IdentityService:AppId"] = OidcTestAuthority.ClientId,
            ["IdentityService:AppSecret"] = OidcTestAuthority.Secret,
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Trace" };
        if (missing is not null) settings[missing] = "";
        using var factory = CreateFactory(settings: settings, configureTestServices: services => {
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => authority);
            // Consume the real host's filters: a standalone factory bypasses the production
            // category protection and cannot verify its registration on unavailable paths.
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(provider => new LoggerFactory([logs], provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>()));
        });
        using var client = Browser(factory);
        var logger = factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.AspNetCore.ProtocolRegression");
        Assert.True(logger.IsEnabled(LogLevel.Trace));
        logger.LogTrace("Protocol regression trace capture is active.");
        Assert.Contains("Protocol regression trace capture is active.", logs.Messages);

        var canaries = new[] { "fictitious-protocol-code-canary", "fictitious-protocol-state-canary",
            "fictitious-protocol-error-canary", "fictitious-protocol-description-canary" };
        var query = "?code=" + canaries[0] + "&state=" + canaries[1]
            + "&error=" + canaries[2] + "&error_description=" + canaries[3];
        // With the hosted login unavailable every entrypoint keeps the fixed 503 contract; with
        // it complete the canary query never produces a sign-in: the callback fails closed and
        // the logout return answers the package's fixed 400 page (issue #70).
        using var callbackResponse = await client.GetAsync(AdminOidcConstants.CallbackPath + query);
        Assert.Equal(missing is null ? HttpStatusCode.Redirect : HttpStatusCode.ServiceUnavailable, callbackResponse.StatusCode);
        using var returnResponse = await client.GetAsync(AdminOidcConstants.LogoutReturnPath + query);
        Assert.Equal(missing is not null ? HttpStatusCode.ServiceUnavailable
            : HttpStatusCode.BadRequest, returnResponse.StatusCode);
        if (missing is not null)
        {
            foreach (var response in new[] { callbackResponse, returnResponse })
            {
                Assert.Equal("{\"success\":false,\"message\":\"Hosted sign-in is not configured.\"}", await response.Content.ReadAsStringAsync());
                Assert.False(response.Headers.Contains("Set-Cookie"));
            }
        }
        Assert.False(logs.Messages.Any(message => canaries.Any(message.Contains)), "Protocol query values appeared in host logs.");
        Assert.False(logs.Messages.Any(message => message.Contains(OidcTestAuthority.Secret)), "Client credentials appeared in host logs.");
        Assert.False(authority.DiscoveryEntered.Task.IsCompleted);
        Assert.Equal(0, authority.Redeems);
        Assert.Empty(authority.LogoutForms);
        if (missing is null)
        {
            // The package is registered with a complete configuration: no protocol object exists.
            Assert.Equal(0, Tickets(factory).Count);
        }
    }

    [Theory]
    [InlineData("IdentityService:Authority")][InlineData("IdentityService:AppId")]
    [InlineData("IdentityService:AppSecret")][InlineData("AdminOidc:RedirectUri")]
    [InlineData("AdminOidc:PostLogoutRedirectUri")]
    public async Task EachMissingHostedKeyStartsSafelyAndAllSixEntrypointsReturn503(string missing)
    {
        using var authority = new OidcTestAuthority();
        var logs = new OidcLogCapture();
        var settings = new Dictionary<string, string?> {
            ["AdminOidc:Enabled"] = "false", ["AdminOidc:RedirectUri"] = OidcTestAuthority.RedirectUri,
            ["AdminOidc:PostLogoutRedirectUri"] = OidcTestAuthority.PostLogoutUri,
            ["IdentityService:Authority"] = OidcTestAuthority.Issuer, ["IdentityService:AppId"] = OidcTestAuthority.ClientId,
            ["IdentityService:AppSecret"] = OidcTestAuthority.Secret, [missing] = "" };
        using var factory = CreateFactory(settings: settings, configureTestServices: services => {
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => authority);
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(new LoggerFactory([logs]));
        });
        using var client = Browser(factory);
        foreach (var path in new[] { "/admin/auth/oidc/start", AdminOidcConstants.CallbackPath, "/admin/auth/oidc/session",
            "/admin/auth/oidc/csrf", AdminOidcConstants.LogoutReturnPath, "/admin/auth/oidc/logout" })
        {
            using var response = await client.SendAsync(new HttpRequestMessage(path.EndsWith("/logout") ? HttpMethod.Post : HttpMethod.Get, path));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"success\":false,\"message\":\"Hosted sign-in is not configured.\"}", await response.Content.ReadAsStringAsync());
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.False(authority.DiscoveryEntered.Task.IsCompleted);
        Assert.Equal(0, authority.Redeems);
        Assert.Empty(authority.LogoutForms);
        Assert.Contains(logs.Messages, value => value.Contains("DOCTHECA_OIDC_NOT_CONFIGURED") && value.Contains(missing));
        Assert.DoesNotContain(logs.Messages, value => value.Contains(OidcTestAuthority.Secret));
    }

    [Theory]
    [InlineData("IdentityService:Authority")][InlineData("IdentityService:Issuer")][InlineData("IdentityService:Audience")]
    public async Task MissingBearerTrustNeverAuthenticatesHeaderOrLegacyCookie(string missing)
    {
        using var factory = CreateFactory(settings: new Dictionary<string, string?> { [missing] = "" });
        using var client = Browser(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add("Authorization", "Bearer " + CreateToken("admin"));
        request.Headers.Add("Cookie", "docthecaAccessToken=" + CreateToken("admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ProtectedApiRoute)).StatusCode);
    }
    [Theory]
    [InlineData("admin", 200)][InlineData("user", 403)][InlineData("invalid", 401)]
    public async Task LegacyCookiesCannotChangeBearerAuthorization(string role, int expected)
    {
        using var factory = CreateFactory();
        using var client = Browser(factory);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add("Authorization", "Bearer " + (role == "invalid" ? "forged-token" : CreateToken(role)));
        request.Headers.Add("Cookie", "docthecaAccessToken=" + CreateToken("admin") + "; docthecaRefreshToken=fictitious-refresh-canary");
        Assert.Equal(expected, (int)(await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("issuer")][InlineData("audience")][InlineData("expired")][InlineData("signature")]
    public async Task CompleteBearerTrustRetainsCryptographicChecks(string defect)
    {
        using var factory = CreateFactory();
        using var client = Browser(factory);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: defect == "issuer" ? "https://wrong.example.test" : TestIssuer,
            audience: defect == "audience" ? "wrong-audience" : TestAudience,
            claims: [new System.Security.Claims.Claim("role", "admin")],
            notBefore: DateTime.UtcNow.AddHours(-1),
            expires: defect == "expired" ? DateTime.UtcNow.AddMinutes(-2) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                defect == "signature" ? new Microsoft.IdentityModel.Tokens.RsaSecurityKey(rsa) : TestSigningKey,
                Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSha256));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add("Authorization", "Bearer " + new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

}
