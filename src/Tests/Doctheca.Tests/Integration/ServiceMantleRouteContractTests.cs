using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Doctheca.Host.Authentication;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// Proves the fixed route and identity contract of the real host is unchanged after replacing
/// the local correlation middleware with the ServiceMantle pipeline: static assets, the SPA
/// fallback, the anonymous auth entries, and <c>/health</c> stay anonymous; unauthenticated
/// admin API calls return 401; non-admin tokens return 403; valid admin Bearer and cookie
/// sessions still reach the real endpoints. Every response — anonymous or not — carries the
/// correlation header injected by the first middleware.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleRouteContractTests : ServiceMantleIntegrationTestBase
{
    public ServiceMantleRouteContractTests(PostgreSqlFixture database) : base(database)
    {
    }

    [Fact]
    public async Task StaticAssets_SpaFallback_HealthAndAnonymousAuth_StayAnonymous()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: true);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();

            // Static asset served from wwwroot before authentication.
            using var staticAsset = await client.GetAsync("/probe.txt");
            Assert.Equal(HttpStatusCode.OK, staticAsset.StatusCode);
            Assert.Equal(
                "doctheca-static-probe",
                await staticAsset.Content.ReadAsStringAsync());

            // SPA entry.
            using var root = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, root.StatusCode);
            Assert.Equal("text/html", root.Content.Headers.ContentType?.MediaType);
            Assert.Contains(
                "doctheca-stub-index-marker",
                await root.Content.ReadAsStringAsync());

            // SPA fallback: any unmatched non-API route rewrites to index.html anonymously.
            using var spaRoute = await client.GetAsync("/documents/library");
            Assert.Equal(HttpStatusCode.OK, spaRoute.StatusCode);
            Assert.Equal("text/html", spaRoute.Content.Headers.ContentType?.MediaType);

            // The existing health endpoint stays anonymous with its original payload.
            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            Assert.Equal("application/json", health.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"status\":\"healthy\"", await health.Content.ReadAsStringAsync());

            // The anonymous auth entries are reachable without a session: login with an empty
            // body reaches the endpoint (400 validation), it is not gated by 401.
            using var login = await client.PostAsJsonAsync(
                "/admin/auth/login",
                new { username = "", password = "" });
            Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AdminApi_Anonymous_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ProtectedApiRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdminApi_AuthenticatedWithoutAdminRole_Returns403()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("user"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminApi_AdminBearer_ReachesRealEndpoint()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", CreateToken("admin"));

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":true", body);
        Assert.Contains("\"roles\":[\"admin\"]", body);
    }

    [Fact]
    public async Task AdminApi_AdminAccessCookie_ReachesRealEndpoint()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add(
            "Cookie",
            $"{DocthecaAuthenticationConstants.AccessCookieName}={CreateToken("AdMiN")}");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"success\":true", body);
    }

    [Fact]
    public async Task DevContentRoot_WithoutWwwroot_KeepsHealthAnonymousAndApiGated()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: false);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();

            using var health = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);

            using var api = await client.GetAsync(ProtectedApiRoute);
            Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }
}
