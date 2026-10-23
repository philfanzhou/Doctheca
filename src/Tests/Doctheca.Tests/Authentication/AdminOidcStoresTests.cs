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
    public static IEnumerable<object[]> TransportProfiles()
    {
        foreach (var environment in new[] { "Production", "Staging", "Development", "Testing" })
        foreach (var scheme in new[] { "http", "https" })
        foreach (var host in new[] { "192.0.2.10", "admin.example.test", "localhost", "127.0.0.1", "[::1]" })
        foreach (var authorityScheme in new[] { "http", "https" })
            yield return [environment, $"{scheme}://{host}:5012", authorityScheme];
    }

    private static IConfiguration Configuration(string origin = "https://admin.example.test",
        string authority = "https://identity.example.test/") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> {
            ["AdminOidc:RedirectUri"] = origin + AdminOidcConstants.CallbackPath,
            ["AdminOidc:PostLogoutRedirectUri"] = origin + AdminOidcConstants.LogoutReturnPath,
            ["IdentityService:Authority"] = authority, ["IdentityService:AppId"] = "fake-app",
            ["IdentityService:AppSecret"] = "fictitious-secret-canary"
        }).Build();

    [Theory]
    [MemberData(nameof(TransportProfiles))]
    public void ConfigurationAcceptsBothTransportsInEveryEnvironment(string environment, string origin, string authorityScheme)
    {
        var authority = authorityScheme + "://identity.example.test";
        var settings = AdminOidcSettings.Read(Configuration(origin, authority + "/"),
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment });
        Assert.True(settings.Available);
        Assert.Equal(authority, settings.Authority);
        Assert.Equal(origin + AdminOidcConstants.CallbackPath, settings.RedirectUri);
        Assert.Equal(origin + AdminOidcConstants.LogoutReturnPath, settings.PostLogoutRedirectUri);
    }

    public static IEnumerable<object[]> InvalidCallbackUris()
    {
        foreach (var scheme in new[] { "http", "https" })
        foreach (var path in new[] { AdminOidcConstants.CallbackPath, AdminOidcConstants.LogoutReturnPath })
        {
            var canonical = scheme + "://admin.example.test" + path;
            foreach (var value in new[] {
                "ftp://admin.example.test" + path, path, "//admin.example.test" + path,
                canonical + "/", canonical + "?code=fictitious-query-canary", canonical + "#fictitious-fragment-canary",
                scheme + "://user@admin.example.test" + path, scheme + "://*.example.test" + path,
                canonical + "\r\n", canonical.Replace("/admin/", "/Admin/"),
                scheme + "://admin.example.test/wrong", canonical.Replace("admin.example.test", "例.example.test"),
                canonical.Replace("admin.example.test", "ADMIN.example.test"), canonical.Replace("/admin/", "/a%64min/") })
                yield return [scheme, path == AdminOidcConstants.CallbackPath ? "AdminOidc:RedirectUri" : "AdminOidc:PostLogoutRedirectUri", value];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidCallbackUris))]
    public void BothTransportsRetainStructuralCallbackValidation(string scheme, string key, string value)
    {
        var config = Configuration(scheme + "://admin.example.test");
        config[key] = value;
        Assert.Equal(key, Assert.Throws<InvalidOperationException>(() => AdminOidcSettings.Read(config,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" })).Message);
    }

    [Theory]
    [InlineData("http")][InlineData("https")]
    public void RegisteredUriLengthBoundaryRemainsExact(string scheme)
    {
        var suffix = ".example.test" + AdminOidcConstants.LogoutReturnPath;
        var prefix = scheme + "://";
        var host = string.Join('.', Enumerable.Repeat(new string('a', 50), 8));
        var padding = 500 - prefix.Length - host.Length - suffix.Length - 1;
        var postLogout = prefix + host + "." + new string('b', padding) + suffix;
        var config = Configuration(prefix + host + "." + new string('b', padding) + ".example.test");
        Assert.Equal(500, postLogout.Length);
        Assert.True(AdminOidcSettings.Read(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment()).Available);
        config["AdminOidc:PostLogoutRedirectUri"] = postLogout.Replace(".example.test", "c.example.test");
        Assert.Equal("AdminOidc:PostLogoutRedirectUri", Assert.Throws<InvalidOperationException>(() =>
            AdminOidcSettings.Read(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment())).Message);
        config["AdminOidc:PostLogoutRedirectUri"] = postLogout;
        var callback = config["AdminOidc:RedirectUri"]!;
        config["AdminOidc:RedirectUri"] = callback.Replace(".example.test", new string('c', 501 - callback.Length) + ".example.test");
        Assert.Equal("AdminOidc:RedirectUri", Assert.Throws<InvalidOperationException>(() =>
            AdminOidcSettings.Read(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment())).Message);
    }

    [Theory]
    [InlineData("http")][InlineData("https")]
    public void PostLogoutOriginMustMatchSchemeHostAndPort(string scheme)
    {
        foreach (var origin in new[] { (scheme == "http" ? "https" : "http") + "://admin.example.test", scheme + "://other.example.test", scheme + "://admin.example.test:5012" })
        {
            var config = Configuration(scheme + "://admin.example.test");
            config["AdminOidc:PostLogoutRedirectUri"] = origin + AdminOidcConstants.LogoutReturnPath;
            Assert.Equal("AdminOidc:PostLogoutRedirectUri", Assert.Throws<InvalidOperationException>(() =>
                AdminOidcSettings.Read(config, new Microsoft.Extensions.Hosting.Internal.HostingEnvironment())).Message);
        }
    }

    [Theory]
    [InlineData("ftp://identity.example.test")][InlineData("/identity")]
    [InlineData("https://user@identity.example.test")][InlineData("http://identity.example.test?query=canary")]
    [InlineData("https://identity.example.test#canary")][InlineData("http://*.example.test")]
    [InlineData("https://identity.example.test\n")]
    public void AuthorityRetainsIndependentStructuralValidation(string authority)
    {
        Assert.Equal("IdentityService:Authority", Assert.Throws<InvalidOperationException>(() =>
            AdminOidcSettings.Read(Configuration(authority: authority), new Microsoft.Extensions.Hosting.Internal.HostingEnvironment())).Message);
    }

    [Theory]
    [InlineData("http", "https")][InlineData("https", "http")][InlineData("http", "http")]
    public void BearerHttpMetadataStillRequiresExplicitFalse(string authorityScheme, string issuerScheme)
    {
        var options = new Doctheca.Common.Authentication.IdentityAuthenticationOptions {
            Authority = authorityScheme + "://fictitious-authority-canary.test",
            Issuer = issuerScheme + "://fictitious-issuer-canary.test", Audience = "fake-app"
        };
        var validator = new Doctheca.Common.Authentication.IdentityAuthenticationOptionsValidator();
        var rejected = validator.Validate(null, options);
        Assert.True(rejected.Failed);
        Assert.Contains(rejected.Failures!, value => value.Contains("IdentityService:RequireHttpsMetadata"));
        Assert.DoesNotContain("fictitious-", string.Join(' ', rejected.Failures!));
        options.RequireHttpsMetadata = false;
        Assert.True(validator.Validate(null, options).Succeeded);
    }

    [Theory]
    [InlineData("http")][InlineData("https")]
    public void EveryMissingHostedKeyRemainsUnavailableWithKeyNamesOnly(string scheme)
    {
        foreach (var key in new[] { "AdminOidc:RedirectUri", "AdminOidc:PostLogoutRedirectUri", "IdentityService:Authority", "IdentityService:AppId", "IdentityService:AppSecret" })
        {
            var config = Configuration(scheme + "://admin.example.test");
            config[key] = null;
            var settings = AdminOidcSettings.Read(config,
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Production" });
            Assert.False(settings.Available);
            Assert.Equal(new[] { key }, settings.MissingKeys);
            Assert.Equal("AdminOidcSettings", settings.ToString());
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
    [InlineData("-1", true)][InlineData("301", true)][InlineData("fictitious-skew-canary", true)]
    public void CompleteInvalidSkewFailsWithKeyOnly(string skew, bool http = false)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AdminOidc:RedirectUri"] = "https://admin.example.test/admin/auth/oidc/callback",
            ["AdminOidc:PostLogoutRedirectUri"] = "https://admin.example.test/admin/auth/oidc/logout/return",
            ["IdentityService:Authority"] = "https://identity.example.test", ["IdentityService:AppId"] = "fake-app",
            ["IdentityService:AppSecret"] = "fictitious-secret-canary", ["IdentityService:ClockSkewSeconds"] = skew
        }).Build();
        if (http)
        {
            config["AdminOidc:RedirectUri"] = config["AdminOidc:RedirectUri"]!.Replace("https:", "http:");
            config["AdminOidc:PostLogoutRedirectUri"] = config["AdminOidc:PostLogoutRedirectUri"]!.Replace("https:", "http:");
        }
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
    [InlineData(SignaCoreSignInReason.UserCanceled, "/?authError=notAdmin")]
    [InlineData(SignaCoreSignInReason.PreSignInDenied, "/?authError=notAdmin")]
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
