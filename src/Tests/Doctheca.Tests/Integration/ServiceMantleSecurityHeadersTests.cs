using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using ServiceMantle.Web.Http;
using Doctheca.Common.Oss;
using Doctheca.Database;
using Doctheca.Database.Entities;
using Doctheca.Domain.Models;
using Doctheca.Domain.Repositories;
using Moq;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// ServiceMantle security response-header baseline on the real host: exactly the 13 JSON admin
/// endpoints are marked and carry the six immutable single-value headers on success (2xx),
/// business failure (4xx), and 401/403 challenges alike; a downstream component that pre-writes
/// duplicate or wrong values converges back to the single baseline value because the library
/// assigns the headers via OnStarting; and the unmarked rendering contracts — static SPA, SPA
/// fallback, the health endpoint, HTML preview, ZIP download, and image bytes — never receive
/// the JSON CSP baseline, so their existing rendering keeps working.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed class ServiceMantleSecurityHeadersTests : ServiceMantleIntegrationTestBase
{
    // The six immutable header values (verbatim from the library baseline).
    private const string CspValue =
        "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    private static readonly (string Name, string Value)[] Baseline =
    [
        ("Cache-Control", "no-store"),
        ("Pragma", "no-cache"),
        ("X-Content-Type-Options", "nosniff"),
        ("X-Frame-Options", "DENY"),
        ("Referrer-Policy", "no-referrer"),
        ("Content-Security-Policy", CspValue),
    ];

    // The exact 13 marked JSON admin endpoints (method + normalized route pattern).
    private static readonly HashSet<string> ExpectedMarked = new(StringComparer.Ordinal)
    {
        "POST /admin/auth/login",
        "POST /admin/auth/refresh",
        "POST /admin/auth/logout",
        "GET /admin/auth/session",
        "POST /admin/document-files/upload",
        "GET /admin/document-files",
        "GET /admin/document-files/{id:guid}",
        "POST /admin/document-files/{id:guid}/parse",
        "PUT /admin/document-files/{id:guid}/metadata",
        "DELETE /admin/document-files/{id:guid}",
        "GET /admin/document-parses",
        "DELETE /admin/document-parses/{parseId:guid}",
        "GET /admin/documents/search",
    };

    // Endpoints that must exist but must NOT carry the marker (rendering contracts).
    private static readonly string[] ExpectedUnmarked =
    [
        "GET /admin/document-parses/{parseId:guid}/images/{imageId:guid}/content",
        "GET /admin/document-files/{id:guid}/export/markdown",
        "GET /admin/document-files/{id:guid}/export/html",
        "GET /admin/document-parses/{parseId:guid}/export/markdown",
        "GET /admin/document-parses/{parseId:guid}/export/html",
        "GET /health",
    ];

    public ServiceMantleSecurityHeadersTests(PostgreSqlFixture database) : base(database)
    {
    }

    [Fact]
    public void MarkedEndpoints_AreExactlyTheThirteenJsonAdminEndpoints()
    {
        using var factory = CreateFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
                (
                    Key: Normalize(endpoint),
                    Marked: endpoint.Metadata.GetMetadata<SecurityResponseHeadersMetadata>()
                        is not null))
            .ToList();

        var marked = endpoints.Where(pair => pair.Marked).Select(pair => pair.Key).ToHashSet();
        var all = endpoints.Select(pair => pair.Key).ToHashSet();

        // Exactly the 13 JSON admin endpoints are marked — no more, no fewer.
        Assert.True(
            marked.SetEquals(ExpectedMarked),
            $"marked set mismatch.\n  extra: {string.Join(", ", marked.Except(ExpectedMarked))}\n" +
            $"  missing: {string.Join(", ", ExpectedMarked.Except(marked))}");

        // The rendering-contract endpoints exist and are unmarked.
        foreach (var unmarked in ExpectedUnmarked)
        {
            Assert.Contains(unmarked, all);
            Assert.DoesNotContain(unmarked, marked);
        }
    }

    [Fact]
    public async Task MarkedEndpoints_CarrySixSingleValueHeaders_OnSuccessFailureAnd401403()
    {
        // The fixture's loose ISearchIndexService mock returns a null result list, which the
        // search endpoint dereferences; configure an explicit empty-result double so the
        // marked search endpoint answers a real 200.
        var searchIndex = new Mock<ISearchIndexService>();
        searchIndex
            .Setup(index => index.ExactSearchAsync(
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<SearchFilterModel?>(),
                It.IsAny<int>(),
                It.IsAny<string?>()))
            .ReturnsAsync((new List<SearchResultModel>(), 0, null));
        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<ISearchIndexService>();
            services.AddSingleton(searchIndex.Object);
        });
        using var client = factory.CreateClient();

        // login with an empty body reaches the endpoint and fails validation (400) — the
        // challenge/error still carries the baseline.
        using (var login = await client.PostAsJsonAsync(
                   "/admin/auth/login", new { username = "", password = "" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
            AssertBaseline(login);
        }

        // session: 401 anonymous, 403 non-admin, 200 admin — all carry the baseline.
        using (var anon = await client.GetAsync("/admin/auth/session"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
            AssertBaseline(anon);
            // The 401 stays a JSON challenge, not the SPA page.
            Assert.NotEqual("text/html", anon.Content.Headers.ContentType?.MediaType);
        }

        using (var forbidden = await SendAsync(client, "/admin/auth/session", "user"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            AssertBaseline(forbidden);
        }

        using (var session = await SendAsync(client, "/admin/auth/session", "admin"))
        {
            Assert.Equal(HttpStatusCode.OK, session.StatusCode);
            AssertBaseline(session);
        }

        // A marked API list endpoint (200 admin).
        using (var list = await SendAsync(client, "/admin/document-files", "admin"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            AssertBaseline(list);
        }

        // A marked search endpoint (200 admin).
        using (var search = await SendAsync(
                   client, "/admin/documents/search?query=probe", "admin"))
        {
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            AssertBaseline(search);
        }
    }

    [Fact]
    public async Task DownstreamDuplicateOrOverride_ConvergesToSingleBaselineValue()
    {
        // A test-only startup filter pre-writes duplicate/wrong values on the marked session
        // route (and stamps a sentinel header to prove it ran). The library assigns its six
        // headers via OnStarting at response start, so the final response converges to exactly
        // one baseline value each; the sentinel is untouched (the middleware only owns the six).
        using var factory = CreateFactory(configureTestServices: services =>
            services.AddSingleton<IStartupFilter, DuplicateHeaderStartupFilter>());
        using var client = factory.CreateClient();

        using var response = await SendAsync(client, "/admin/auth/session", "admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The filter really ran (self-check), otherwise this test would prove nothing.
        Assert.Equal("1", AllValues(response, "x-test-preset").Single());
        // Duplicate/wrong pre-writes converged back to the single baseline values.
        Assert.Equal("no-store", AllValues(response, "Cache-Control").Single());
        Assert.Equal("DENY", AllValues(response, "X-Frame-Options").Single());
        AssertBaseline(response);
    }

    [Fact]
    public async Task UnmarkedResponses_KeepTheirRenderingContracts_WithoutTheBaseline()
    {
        var contentRoot = CreateTempContentRoot(withWwwrootStub: true);
        try
        {
            using var factory = CreateFactory(contentRoot: contentRoot);
            using var client = factory.CreateClient();

            // Static asset and SPA fallback: HTML/asset rendering, no JSON CSP baseline.
            using (var staticAsset = await client.GetAsync("/probe.txt"))
            {
                Assert.Equal(HttpStatusCode.OK, staticAsset.StatusCode);
                AssertNoBaseline(staticAsset);
            }

            using (var spa = await client.GetAsync("/documents/library"))
            {
                Assert.Equal(HttpStatusCode.OK, spa.StatusCode);
                Assert.Equal("text/html", spa.Content.Headers.ContentType?.MediaType);
                Assert.Contains(
                    "doctheca-stub-index-marker", await spa.Content.ReadAsStringAsync());
                AssertNoBaseline(spa);
            }

            // Health endpoint is unmarked: no JSON CSP baseline.
            using (var health = await client.GetAsync("/health"))
            {
                Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                AssertNoBaseline(health);
            }
        }
        finally
        {
            Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ExportHtml_Zip_AndImageContent_AreUnmarked_AndRenderUnchanged()
    {
        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ossMock = new Mock<IOssService>();
        ossMock
            .Setup(oss => oss.DownloadAsync(It.IsAny<string>()))
            .ReturnsAsync(() => (Stream)new MemoryStream(imageBytes));

        using var factory = CreateFactory(configureTestServices: services =>
        {
            services.RemoveAll<IOssService>();
            services.AddSingleton(ossMock.Object);
        });
        using var client = factory.CreateClient();

        var (fileId, parseId, imageId) = await SeedParsedDocumentWithImageAsync(factory);
        try
        {
            // HTML preview (text/html) — unmarked, no CSP baseline.
            using (var html = await SendAsync(
                       client, $"/admin/document-files/{fileId}/export/html", "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, html.StatusCode);
                Assert.Equal("text/html", html.Content.Headers.ContentType?.MediaType);
                AssertNoBaseline(html);
            }

            // Markdown ZIP download (application/zip) — unmarked, no CSP baseline.
            using (var zip = await SendAsync(
                       client, $"/admin/document-files/{fileId}/export/markdown", "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
                Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
                AssertNoBaseline(zip);
            }

            // Image bytes (image/png) — unmarked, no CSP baseline, content preserved.
            using (var image = await SendAsync(
                       client,
                       $"/admin/document-parses/{parseId}/images/{imageId}/content",
                       "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, image.StatusCode);
                Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
                Assert.Equal(imageBytes, await image.Content.ReadAsByteArrayAsync());
                AssertNoBaseline(image);
            }
        }
        finally
        {
            await DeleteSeededAsync(factory, fileId, parseId, imageId);
        }
    }

    // ── helpers ──

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, string role)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(role));
        return await client.SendAsync(request);
    }

    private static void AssertBaseline(HttpResponseMessage response)
    {
        foreach (var (name, value) in Baseline)
        {
            // Exactly one value, equal to the immutable baseline (no duplicates, no weakening).
            Assert.Equal(value, AllValues(response, name).Single());
        }
    }

    private static void AssertNoBaseline(HttpResponseMessage response)
    {
        // The CSP baseline is the strongest signal; an unmarked response must not carry it.
        Assert.Empty(AllValues(response, "Content-Security-Policy"));
        Assert.Empty(AllValues(response, "X-Frame-Options"));
    }

    private static IEnumerable<string> AllValues(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var headerValues))
        {
            foreach (var value in headerValues)
            {
                yield return value;
            }
        }

        if (response.Content is not null &&
            response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            foreach (var value in contentValues)
            {
                yield return value;
            }
        }
    }

    private static string Normalize(RouteEndpoint endpoint)
    {
        var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is
            { Count: > 0 } methods
            ? methods[0]
            : "ANY";
        var pattern = endpoint.RoutePattern.RawText ?? string.Empty;
        if (pattern.Length > 1)
        {
            pattern = pattern.TrimEnd('/');
        }

        return $"{method} {pattern}";
    }

    private static async Task<(Guid FileId, Guid ParseId, Guid ImageId)>
        SeedParsedDocumentWithImageAsync(WebApplicationFactory<Program> factory)
    {
        var fileId = Guid.NewGuid();
        var parseId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        db.DocumentFiles.Add(new DocumentFileEntity
        {
            Id = fileId,
            FileName = "security-headers-probe.md",
            ContentType = "text/markdown"
        });
        db.DocumentParses.Add(new DocumentParseEntity
        {
            Id = parseId,
            DocumentFileId = fileId,
            ModelVersion = "vlm",
            Status = DocumentParseStatus.Parsed,
            MarkdownContent = "# probe"
        });
        db.DocumentParseImages.Add(new DocumentParseImageEntity
        {
            Id = imageId,
            ParseId = parseId,
            ImageName = "probe.png",
            ImagePath = "legacy/probe.png",
            ContentType = "image/png"
        });
        await db.SaveChangesAsync();
        return (fileId, parseId, imageId);
    }

    private static async Task DeleteSeededAsync(
        WebApplicationFactory<Program> factory, Guid fileId, Guid parseId, Guid imageId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocthecaDbContext>();
        db.DocumentParseImages.RemoveRange(
            db.DocumentParseImages.Where(image => image.Id == imageId));
        db.DocumentParses.RemoveRange(db.DocumentParses.Where(parse => parse.Id == parseId));
        db.DocumentFiles.RemoveRange(db.DocumentFiles.Where(file => file.Id == fileId));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Pre-writes duplicate/wrong values for two baseline headers on the marked session route
    /// and stamps a sentinel header, proving both that the filter ran and that the library's
    /// OnStarting assignment converges the response back to single baseline values.
    /// </summary>
    private sealed class DuplicateHeaderStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    if (context.Request.Path.StartsWithSegments("/admin/auth/session"))
                    {
                        context.Response.Headers["x-test-preset"] = "1";
                        context.Response.Headers["Cache-Control"] =
                            new StringValues(["public", "max-age=999"]);
                        context.Response.Headers["X-Frame-Options"] = "ALLOWALL";
                    }

                    return nextMiddleware();
                });
                next(app);
            };
    }
}
