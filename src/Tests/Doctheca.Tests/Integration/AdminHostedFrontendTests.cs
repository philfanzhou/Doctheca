using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Doctheca.Host.Authentication;
using Doctheca.Tests.Authentication;
using Microsoft.Playwright;
using Xunit;

namespace Doctheca.Tests.Integration;

[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class AdminHostedFrontendTests(PostgreSqlFixture database, MinioFixture minio)
    : ServiceMantleIntegrationTestBase(database), IClassFixture<MinioFixture>
{
    private static readonly Lazy<Task<string>> Spa = new(BuildSpa);

    private static async Task<string> BuildSpa()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "frontend", "package.json"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Frontend source unavailable.");
        foreach (var arguments in new[] { "ci", "run build" })
        {
            using var process = Process.Start(new ProcessStartInfo("npm", arguments) { WorkingDirectory = Path.Combine(root.FullName, "frontend"), RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            Assert.True(process.ExitCode == 0, "Current frontend build failed.");
        }
        return Path.Combine(root.FullName, "wwwroot");
    }

    private static int Port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task<BrowserHarness> Open(string scenario = "admin", IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null, string environment = "Testing", bool nonLoopback = false)
    {
        var spa = await Spa.Value;
        var root = CreateTempContentRoot(false);
        foreach (var source in Directory.GetFiles(spa, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, "wwwroot", Path.GetRelativePath(spa, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
        var appPort = Port();
        var providerPort = Port();
        var origin = $"http://{(nonLoopback ? "admin.doctheca.test" : "127.0.0.1")}:{appPort}";
        var authority = new OidcTestAuthority { AuthorityIssuer = $"http://{(nonLoopback ? "identity.doctheca.test" : "127.0.0.1")}:{providerPort}" };
        if (scenario == "unavailable") authority.DiscoveryDefect = "500";
        if (scenario == "local") authority.LogoutFailure = "500";
        if (scenario == "signInFailed") authority.TokenFailure = "500";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{providerPort}");
        var provider = builder.Build();
        provider.MapGet("/authorize", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            var query = context.Request.Query.ToDictionary(p => p.Key, p => p.Value.ToString());
            var callback = QueryHelpers.AddQueryString(query["redirect_uri"], new Dictionary<string, string?> {
                ["state"] = query["state"], ["iss"] = authority.AuthorityIssuer,
                [scenario == "cancelled" ? "error" : "code"] = scenario == "cancelled" ? "access_denied" : authority.Code(query, accessDefect: scenario == "user" ? "user" : "admin") });
            context.Response.Redirect(callback);
        });
        provider.MapGet("/oauth2/logout", (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            var form = authority.LogoutForms.Last();
            context.Response.Redirect(QueryHelpers.AddQueryString(form["post_logout_redirect_uri"], "state", form["state"]));
        });
        await provider.StartAsync();
        var time = new ManualOidcTime();
        var factory = CreateFactory(root, services =>
        {
            services.AddHttpClient(SignaCore.Client.AspNetCore.SignaCoreHostedLoginDefaults.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => authority);
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
            configureServices?.Invoke(services);
        }, new Dictionary<string, string?>(settings ?? new Dictionary<string, string?>()) {
            ["Endpoints:Http"] = appPort.ToString(), ["AdminOidc:Enabled"] = "true",
            ["AdminOidc:RedirectUri"] = origin + AdminOidcConstants.CallbackPath,
            ["AdminOidc:PostLogoutRedirectUri"] = origin + AdminOidcConstants.LogoutReturnPath,
            ["IdentityService:Authority"] = authority.AuthorityIssuer, ["IdentityService:Issuer"] = authority.AuthorityIssuer,
            ["IdentityService:AppId"] = OidcTestAuthority.ClientId, ["IdentityService:AppSecret"] = OidcTestAuthority.Secret }, environment);
        factory.UseKestrel(appPort);
        factory.StartServer();
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Args = nonLoopback
            ? ["--host-resolver-rules=MAP admin.doctheca.test 127.0.0.1, MAP identity.doctheca.test 127.0.0.1", "--no-proxy-server"] : [] });
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        // No traces, HAR, storage state, request headers or protocol URLs are recorded.
        return new(root, origin, authority, provider, factory, playwright, browser, context, page, time);
    }

    [Fact]
    public async Task ProductionNonLoopbackHttpCompletesLoginCsrfWriteAndPreparedLogout()
    {
        await using var h = await Open(environment: "Production", nonLoopback: true);
        Assert.Equal("Production", h.Factory.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>().EnvironmentName);
        await h.Page.GotoAsync(h.Origin + "/#search");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("检索测试", new() { Exact = true }).First).ToBeVisibleAsync();
        Assert.True(h.Page.Url.EndsWith("/#search", StringComparison.Ordinal), "Deep link was not restored.");
        Assert.Equal("admin.doctheca.test", new Uri(h.Page.Url).Host);
        Assert.False(await h.Page.EvaluateAsync<bool>("window.isSecureContext"));
        Assert.Equal(0, await h.Page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        var statuses = await h.Page.EvaluateAsync<int[]>("""
            async () => {
              const route = '/admin/document-files/00000000-0000-0000-0000-000000000125';
              const read = await fetch('/admin/document-files');
              const missing = await fetch(route, { method: 'DELETE' });
              const pair = await (await fetch('/admin/auth/oidc/csrf')).json();
              const wrong = await fetch(route, { method: 'DELETE', headers: { 'X-CSRF-TOKEN': 'forged-token' } });
              const valid = await fetch(route, { method: 'DELETE', headers: { 'X-CSRF-TOKEN': pair.token } });
              return [read.status, missing.status, wrong.status, valid.status];
            }
            """);
        // A CSRF-authorized DELETE reaches the intended handler and its missing-document result.
        Assert.Equal(new[] { 200, 403, 403, 404 }, statuses);
        var cookies = await h.Context.CookiesAsync();
        foreach (var name in new[] { AdminOidcConstants.SessionCookie, AdminOidcConstants.CsrfCookie })
        {
            var cookie = Assert.Single(cookies, value => value.Name == name);
            Assert.False(cookie.Secure);
            Assert.True(cookie.HttpOnly);
            Assert.Equal("/", cookie.Path);
            Assert.Equal(name == AdminOidcConstants.CsrfCookie ? SameSiteAttribute.Strict : SameSiteAttribute.Lax, cookie.SameSite);
        }
        // A top-level form follows the provider/return redirects in Chromium. The existing
        // SPA button's cross-origin fetch behavior is covered separately below.
        var field = h.Factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>().Value.FormFieldName;
        await h.Page.EvaluateAsync("""
            async field => {
              const pair = await (await fetch('/admin/auth/oidc/csrf')).json();
              const form = document.createElement('form');
              form.method = 'POST'; form.action = '/admin/auth/oidc/logout';
              const input = document.createElement('input');
              input.type = 'hidden'; input.name = field; input.value = pair.token;
              form.append(input); document.body.append(form); form.submit();
            }
            """, field);
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("已退出登录");
        Assert.Single(h.Authority.LogoutForms);
        Assert.Equal(0, TicketCount(h.Factory));
        var remainingNames = (await h.Context.CookiesAsync()).Select(value => value.Name).ToArray();
        Assert.DoesNotContain(remainingNames, name => name == AdminOidcConstants.SessionCookie
            || name == AdminOidcConstants.SessionCookie + "-logout-return" || name.Contains("-login-binding."));
        Assert.Equal(401, await h.Page.EvaluateAsync<int>("async () => (await fetch('/admin/document-files')).status"));
    }

    [Fact]
    public async Task DeepLinkLoginExpiryReauthenticationAndPreparedLogout()
    {
        await using var h = await Open();
        await h.Page.GotoAsync(h.Origin + "/#search");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByText("检索测试", new() { Exact = true }).First).ToBeVisibleAsync();
        Assert.True(h.Page.Url.EndsWith("/#search", StringComparison.Ordinal), "Deep link was not restored.");
        Assert.Equal(1, h.Authority.Redeems);
        Assert.Equal(0, await h.Page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        h.Time.Advance(TimeSpan.FromMinutes(16));
        // A real protected request expires the UI; it is never refreshed or replayed.
        await h.Page.EvaluateAsync("location.hash = '#docs'");
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" })).ToBeVisibleAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("重新认证");
        Assert.Equal(1, h.Authority.Redeems);
        h.Time.Advance(TimeSpan.FromMinutes(-16));
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Button, new() { Name = "退出", Exact = true })).ToBeVisibleAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "退出", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("已退出登录");
        Assert.Single(h.Authority.LogoutForms);
        Assert.False(new Uri(h.Page.Url).Query.Contains("authResult"), "Result was not removed.");
    }

    [Theory]
    [InlineData("user", "没有管理员权限")]
    [InlineData("cancelled", "已取消登录")]
    [InlineData("unavailable", "认证服务暂时不可用")]
    [InlineData("signInFailed", "登录失败")]
    public async Task FixedProviderFailuresStayAtRetryEntry(string scenario, string expected)
    {
        await using var h = await Open(scenario);
        await h.Page.GotoAsync(h.Origin + "/#docs");
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync(expected);
        Assert.False(new Uri(h.Page.Url).Query.Contains("authError"), "Error was not removed.");
        Assert.Equal(0, TicketCount(h.Factory));
    }

    [Fact]
    public async Task UpstreamLogoutFailureShowsOnlyLocalExit()
    {
        await using var h = await Open("local");
        await h.Page.GotoAsync(h.Origin);
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "使用 SignaCore 登录" }).ClickAsync();
        await h.Page.GetByRole(AriaRole.Button, new() { Name = "退出", Exact = true }).ClickAsync();
        await Assertions.Expect(h.Page.GetByRole(AriaRole.Alert)).ToContainTextAsync("本地已退出，SignaCore 会话可能仍然有效");
        Assert.Equal(0, TicketCount(h.Factory));
    }

    private static int TicketCount(WebApplicationFactory<Program> factory) =>
        ((SignaCore.Client.AspNetCore.InMemoryTicketStore)factory.Services
            .GetRequiredService<SignaCore.Client.AspNetCore.ITicketStore>()).Count;

    private sealed record BrowserHarness(string Root, string Origin, OidcTestAuthority Authority, WebApplication Provider,
        WebApplicationFactory<Program> Factory, IPlaywright Playwright, IBrowser Browser, IBrowserContext Context, IPage Page,
        ManualOidcTime Time) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync(); await Browser.DisposeAsync(); Playwright.Dispose();
            await Factory.DisposeAsync(); await Provider.DisposeAsync(); Authority.Dispose(); Directory.Delete(Root, true);
        }
    }
}
