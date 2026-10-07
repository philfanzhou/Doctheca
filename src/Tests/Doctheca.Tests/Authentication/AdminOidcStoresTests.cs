using System.Security.Claims;
using Doctheca.Host.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Doctheca.Tests.Authentication;

internal sealed class ManualOidcTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan span) => _now += span;
}

public sealed class AdminOidcStoresTests
{
    [Theory]
    [InlineData("https://admin.example.test/admin/auth/oidc/callback", "Production", true)]
    [InlineData("http://127.0.0.1:5012/admin/auth/oidc/callback", "Development", true)]
    [InlineData("http://[::1]:5012/admin/auth/oidc/callback", "Testing", true)]
    [InlineData("http://127.0.0.1:5012/admin/auth/oidc/callback", "Production", false)]
    [InlineData("http://localhost:5012/admin/auth/oidc/callback", "Development", false)]
    public void ConfigurationEnforcesEnvironmentTlsAndNumericLoopback(string redirect, string environment, bool valid)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "true", ["AdminOidc:PostLogoutRedirectUri"] = new Uri(new Uri(redirect), AdminOidcConstants.LogoutReturnPath).AbsoluteUri, ["AdminOidc:RedirectUri"] = redirect,
            ["IdentityService:Authority"] = "https://identity.example.test/", ["IdentityService:AppId"] = "fake-app", ["IdentityService:AppSecret"] = "fake-secret"
        }).Build();
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };
        if (valid)
        {
            var settings = AdminOidcSettings.Read(config, env);
            Assert.True(settings.Available);
            Assert.Equal("https://identity.example.test", settings.Authority);
            Assert.Equal(redirect.StartsWith("http:"), settings.InsecureLoopback);
        }
        else
        {
            Assert.Equal("AdminOidc:RedirectUri", Assert.Throws<InvalidOperationException>(() => AdminOidcSettings.Read(config, env)).Message);
        }
    }

    [Theory]
    [InlineData("", "IdentityService:AppId")]
    [InlineData("fake-app", "IdentityService:AppSecret")]
    public void MissingCredentialsAreUnavailableWithKeyNamesOnly(string appId, string expectedKey)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "true", ["AdminOidc:PostLogoutRedirectUri"] = "https://admin.example.test/admin/auth/oidc/logout/return", ["AdminOidc:RedirectUri"] = "https://admin.example.test/admin/auth/oidc/callback",
            ["IdentityService:Authority"] = "https://identity.example.test", ["IdentityService:AppId"] = appId, ["IdentityService:AppSecret"] = appId == "" ? "fake-secret" : ""
        }).Build();
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        var settings = AdminOidcSettings.Read(config, env);
        Assert.False(settings.Available);
        Assert.Contains(expectedKey, settings.MissingKeys);
    }

    [Fact]
    public void DisabledConfigurationNeedsNothingElse()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var settings = AdminOidcSettings.Read(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" });
        Assert.False(settings.Available);
        Assert.Equal("AdminOidcSettings", settings.ToString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("/", true)]
    [InlineData("/admin/document-files", true)]
    [InlineData("/admin/document-files?page=2", true)]
    [InlineData("//external.example", false)]
    [InlineData("/\\external.example", false)]
    [InlineData("https://external.example", false)]
    [InlineData("http://127.0.0.1:5012/admin", false)]
    [InlineData("/%2f%2fexternal.example", false)]
    [InlineData("/%255cexternal.example", false)]
    [InlineData("/admin/auth/oidc/start", false)]
    [InlineData("/%61dmin/auth/oidc/callback", false)]
    [InlineData("/safe%0d%0aLocation:external", false)]
    [InlineData("/safe/../admin/auth/login", false)]
    [InlineData("/safe/%2e%2e/admin/auth/logout", false)]
    [InlineData("/", false, 2049)]
    public void ReturnUrlsAcceptOnlySameSiteAbsolutePaths(string? value, bool valid, int pad = 0)
    {
        if (pad > 0) value = "/" + new string('a', pad);
        Assert.Equal(valid, AdminOidcSettings.IsValidReturnUrl(value));
    }

    [Theory]
    [InlineData("https://external.example/admin/auth/oidc/logout/return")]
    [InlineData("https://admin.example.test/wrong")][InlineData("https://admin.example.test/admin/auth/oidc/logout/return?state=x")]
    public void PostLogoutRegistrationIsRequiredExactAndSameOrigin(string postLogout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "true", ["AdminOidc:RedirectUri"] = "https://admin.example.test/admin/auth/oidc/callback",
            ["AdminOidc:PostLogoutRedirectUri"] = postLogout,
            ["IdentityService:Authority"] = "https://identity.example.test", ["IdentityService:AppId"] = "fake-app", ["IdentityService:AppSecret"] = "fake-secret"
        }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        Assert.Equal("AdminOidc:PostLogoutRedirectUri", Assert.Throws<InvalidOperationException>(() => AdminOidcSettings.Read(config, environment)).Message);
    }

    [Theory]
    [InlineData("-1")][InlineData("301")][InlineData("fictitious-skew-canary")]
    public void CompleteInvalidSkewFailsWithKeyOnly(string skew)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AdminOidc:RedirectUri"] = "https://admin.example.test/admin/auth/oidc/callback",
            ["AdminOidc:PostLogoutRedirectUri"] = "https://admin.example.test/admin/auth/oidc/logout/return",
            ["IdentityService:Authority"] = "https://identity.example.test", ["IdentityService:AppId"] = "fake-app",
            ["IdentityService:AppSecret"] = "fictitious-secret-canary", ["IdentityService:ClockSkewSeconds"] = skew
        }).Build();
        var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" };
        Assert.Equal("IdentityService:ClockSkewSeconds", Assert.Throws<InvalidOperationException>(() => AdminOidcSettings.Read(config, environment)).Message);
    }

    /// <summary>
    /// The fixed presentation mapping of the package's closed reason set (issue #70). The
    /// decision input itself is package-internal; the gate's Allowed/Denied outcomes are covered
    /// end-to-end by the integration suite (admin flow, non-admin denial, decision timeout).
    /// </summary>
    [Theory]
    [InlineData(SignaCoreSignInReason.AccessDenied, "/?authError=notAdmin")]
    [InlineData(SignaCoreSignInReason.AuthorityUnreachable, "/?authError=identityUnavailable")]
    [InlineData(SignaCoreSignInReason.StateMismatch, "/?authError=signInFailed")]
    [InlineData(SignaCoreSignInReason.IssuerMismatch, "/?authError=signInFailed")]
    [InlineData(SignaCoreSignInReason.InvalidResponse, "/?authError=signInFailed")]
    [InlineData(SignaCoreSignInReason.TokenExchangeFailed, "/?authError=signInFailed")]
    [InlineData(SignaCoreSignInReason.InvalidToken, "/?authError=signInFailed")]
    [InlineData(SignaCoreSignInReason.SessionStoreFull, "/?authError=signInFailed")]
    public void SignInFailuresRedirectToBoundedSpaTargets(SignaCoreSignInReason reason, string expected)
    {
        Assert.Equal(expected, AdminLoginResponseWriter.RedirectTarget(reason));
    }

    [Fact]
    public async Task InvalidReturnUrlAnswersFixed400Body()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        await AdminLoginResponseWriter.Instance.WriteSignInFailureAsync(
            context, SignaCoreSignInReason.InvalidReturnUrl, CancellationToken.None);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var document = System.Text.Json.JsonDocument.Parse(body.ToArray());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(AdminLoginResponseWriter.InvalidReturnUrlMessage,
            document.RootElement.GetProperty("message").GetString());
    }
}
