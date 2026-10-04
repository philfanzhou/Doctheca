using System.Security.Claims;
using Doctheca.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    [Fact]
    public async Task StateIsCompactSingleUseAtomicBoundedAndExpiring()
    {
        var time = new ManualOidcTime();
        var store = new CompactStateDataFormat(time);
        var key = store.Protect(new AuthenticationProperties { RedirectUri = "/safe" });
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key);
        var consumers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.Unprotect(key))));
        Assert.Single(consumers, result => result is not null);
        key = store.Protect(new());
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(store.Unprotect(key));
        Assert.Equal(0, store.Count);
        for (var i = 0; i < CompactStateDataFormat.Capacity; i++) store.Protect(new());
        Assert.Equal("oidc.state_capacity", Assert.Throws<InvalidOperationException>(() => store.Protect(new())).Message);
        time.Advance(TimeSpan.FromMinutes(5));
        store.RemoveExpired();
        Assert.Equal(0, store.Count);
        Assert.NotNull(store.Protect(new()));
    }

    [Fact]
    public async Task TicketIsSnapshotAbsoluteExpiredRemovedAndRenewCannotResurrect()
    {
        var time = new ManualOidcTime();
        var store = new MemoryTicketStore(time);
        var ticket = Ticket();
        var key = await store.StoreAsync(ticket);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key);
        ticket.Properties.Items["canary"] = "modified-after-store";
        Assert.False((await store.RetrieveAsync(key))!.Properties.Items.ContainsKey("canary"));
        var deadline = (await store.RetrieveAsync(key))!.Properties.ExpiresUtc;
        time.Advance(TimeSpan.FromHours(1));
        ticket.Properties.ExpiresUtc = time.GetUtcNow() + TimeSpan.FromHours(8);
        await store.RenewAsync(key, ticket);
        Assert.Equal(deadline, (await store.RetrieveAsync(key))!.Properties.ExpiresUtc);
        await Task.WhenAll(Task.Run(() => store.RemoveAsync(key)), Task.Run(() => store.RenewAsync(key, ticket)));
        Assert.Null(await store.RetrieveAsync(key));
        await store.RenewAsync(key, ticket);
        Assert.Null(await store.RetrieveAsync(key));
        key = await store.StoreAsync(Ticket());
        time.Advance(TimeSpan.FromHours(8));
        Assert.Null(await store.RetrieveAsync(key));
        Assert.Equal(0, store.Count);
        var cancellation = new CancellationToken(true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => store.StoreAsync(Ticket(), new DefaultHttpContext(), cancellation));
    }

    [Fact]
    public async Task TicketDeadlineIsCappedByAccessTokenExpiry()
    {
        var time = new ManualOidcTime();
        var store = new MemoryTicketStore(time);
        var ticket = Ticket();
        ticket.Properties.ExpiresUtc = time.GetUtcNow() + TimeSpan.FromMinutes(15);
        var key = await store.StoreAsync(ticket);
        var stored = await store.RetrieveAsync(key);
        Assert.NotNull(stored);
        // TicketSerializer rounds the persisted instant to whole seconds.
        Assert.True((time.GetUtcNow() + TimeSpan.FromMinutes(15)) - stored!.Properties.ExpiresUtc! < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TicketCapacityIsBoundedAndSweepReleasesCapacity()
    {
        var time = new ManualOidcTime();
        var store = new MemoryTicketStore(time);
        for (var i = 0; i < MemoryTicketStore.Capacity; i++) await store.StoreAsync(Ticket());
        Assert.Equal("oidc.session_capacity", (await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(Ticket()))).Message);
        time.Advance(TimeSpan.FromHours(8));
        store.RemoveExpired();
        Assert.Equal(0, store.Count);
        Assert.NotNull(await store.StoreAsync(Ticket()));
    }

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

    [Fact]
    public async Task RevocationReturnsOneSnapshotAndRenewCannotRestoreIt()
    {
        var time = new ManualOidcTime();
        var store = new MemoryTicketStore(time);
        var ticket = Ticket();
        ticket.Properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = "fictitious-token-canary" }]);
        var key = await store.StoreAsync(ticket);
        ticket.Properties.StoreTokens([]);
        var revoked = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.Revoke(key))));
        Assert.Equal("fictitious-token-canary", Assert.Single(revoked, snapshot => snapshot is not null)!.Properties.GetTokenValue("id_token"));
        await store.RenewAsync(key, ticket);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public void LogoutReturnStoreIsBoundedSingleUseExpiringAndBrowserBound()
    {
        var time = new ManualOidcTime();
        var store = new LogoutReturnStore(time);
        var pending = store.Create();
        Assert.False(store.Consume(pending.State, new string('x', 43)));
        Assert.False(store.Consume(pending.State, pending.Binding));
        pending = store.Create();
        Assert.True(store.Consume(pending.State, pending.Binding));
        Assert.False(store.Consume(pending.State, pending.Binding));
        pending = store.Create();
        time.Advance(LogoutReturnStore.Lifetime);
        Assert.False(store.Consume(pending.State, pending.Binding));
        for (var n = 0; n < LogoutReturnStore.Capacity; n++) store.Create();
        Assert.Throws<InvalidOperationException>(() => store.Create());
        time.Advance(LogoutReturnStore.Lifetime);
        store.RemoveExpired();
        Assert.Equal(0, store.Count);
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

    private static AuthenticationTicket Ticket() => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "fake")], "test")), new(), AdminOidcConstants.SessionScheme);
}
