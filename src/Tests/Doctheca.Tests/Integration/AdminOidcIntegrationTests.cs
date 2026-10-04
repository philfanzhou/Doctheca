using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Host.Authentication;
using Doctheca.Tests.Authentication;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// End-to-end hosted-login tests against the real Program.cs with an in-process fake SignaCore
/// (see <see cref="OidcTestAuthority"/>). Covers the issue #47 acceptance items: the full code
/// flow, non-admin denial, state/iss/nonce failures, code replay, upstream unavailability,
/// cancellation, invalid return URLs, the disabled-by-default contract, the CSRF boundary, and
/// the fixed re-authentication result after the access token expires.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class AdminOidcIntegrationTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private WebApplicationFactory<Program> OidcFactory(OidcTestAuthority authority, ManualOidcTime? time = null,
        Action<IServiceCollection>? configure = null)
        => CreateFactory(configureTestServices: services =>
        {
            services.Configure<OpenIdConnectOptions>(AdminOidcConstants.OidcScheme, options =>
                options.Backchannel = new HttpClient(authority, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(2) });
            if (time is not null) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
            configure?.Invoke(services);
        }, settings: new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "true", ["AdminOidc:PostLogoutRedirectUri"] = OidcTestAuthority.PostLogoutUri,
            ["AdminOidc:RedirectUri"] = OidcTestAuthority.RedirectUri,
            ["IdentityService:Authority"] = OidcTestAuthority.Issuer,
            ["IdentityService:Issuer"] = OidcTestAuthority.Issuer,
            ["IdentityService:AppId"] = OidcTestAuthority.ClientId,
            ["IdentityService:AppSecret"] = OidcTestAuthority.Secret
        });

    private static HttpClient Browser(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://admin.example.test"), AllowAutoRedirect = false, HandleCookies = false
    });

    private sealed record Handshake(Dictionary<string, string> Query, string Cookies, HttpResponseMessage Response);

    private static async Task<Handshake> Start(HttpClient client, string target = "/admin/document-files")
    {
        var response = await client.GetAsync("/admin/auth/oidc/start?returnUrl=" + Uri.EscapeDataString(target));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(OidcTestAuthority.Issuer + "/authorize", response.Headers.Location!.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        var cookies = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        return new(query, cookies, response);
    }

    private static Task<HttpResponseMessage> Callback(HttpClient client, Handshake handshake, string? code, string? query = null,
        string? cookies = null, CancellationToken cancellation = default)
    {
        query ??= QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, new Dictionary<string, string?>
        {
            ["code"] = code, ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer
        });
        var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Add("Cookie", cookies ?? handshake.Cookies);
        return client.SendAsync(request, cancellation);
    }

    private static void Failed(HttpResponseMessage response, string target = AdminOidcRegistration.SignInFailedRedirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target, response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")));
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")).Split(';')[0];

    [Fact]
    public async Task FullCodeFlowUsesExactProtocolAndServerTicketOnly()
    {
        using var authority = new OidcTestAuthority();
        var logs = new OidcLogCapture();
        using var factory = OidcFactory(authority, configure: services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Trace);
            services.AddSingleton<ILoggerFactory>(provider => new LoggerFactory([logs], provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>()));
        });
        using var client = Browser(factory);
        var handshake = await Start(client);
        Assert.Equal(new[] { "client_id", "code_challenge", "code_challenge_method", "nonce", "redirect_uri", "response_type", "scope", "state" }, handshake.Query.Keys.Order().ToArray());
        Assert.Equal("code", handshake.Query["response_type"]);
        Assert.Equal("openid profile", handshake.Query["scope"]);
        Assert.Equal("S256", handshake.Query["code_challenge_method"]);
        Assert.Equal(OidcTestAuthority.RedirectUri, handshake.Query["redirect_uri"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", handshake.Query["state"]);
        Assert.Matches("^[A-Za-z0-9._~-]{22,128}$", handshake.Query["nonce"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", handshake.Query["code_challenge"]);
        foreach (var cookie in handshake.Response.Headers.GetValues("Set-Cookie"))
        {
            Assert.Contains("httponly", cookie.ToLowerInvariant());
            Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
            Assert.Contains("secure", cookie.ToLowerInvariant());
        }

        var code = authority.Code(handshake.Query);
        using var response = await Callback(client, handshake, code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/document-files", response.Headers.Location!.OriginalString);
        var form = Assert.Single(authority.TokenForms);
        Assert.Equal(new[] { "client_id", "client_secret", "code", "code_verifier", "grant_type", "redirect_uri" }, form.Keys.Order().ToArray());
        Assert.Equal(OidcTestAuthority.Secret, form["client_secret"]);
        Assert.Equal(OidcTestAuthority.ClientId, form["client_id"]);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(OidcTestAuthority.RedirectUri, form["redirect_uri"]);
        Assert.Null(Assert.Single(authority.AuthorizationHeaders));

        var cookieValue = SessionCookie(response);
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AdminOidcConstants.SessionScheme);
        Assert.False(cookieOptions.SlidingExpiration);
        Assert.IsType<MemoryTicketStore>(cookieOptions.SessionStore);
        var referenceTicket = cookieOptions.TicketDataFormat.Unprotect(cookieValue[(cookieValue.IndexOf('=') + 1)..])!;
        Assert.NotNull(referenceTicket);
        Assert.Empty(referenceTicket.Properties.GetTokens());
        var reference = Assert.Single(referenceTicket.Principal.Claims).Value;
        var stored = await factory.Services.GetRequiredService<MemoryTicketStore>().RetrieveAsync(reference);
        Assert.NotNull(stored);
        Assert.Equal(authority.LastAccessToken, stored!.Properties.GetTokenValue("access_token"));
        Assert.Equal(authority.LastIdToken, stored.Properties.GetTokenValue("id_token"));
        Assert.NotNull(stored.Properties.GetTokenValue("expires_at"));
        Assert.Equal("fake-subject", stored.Principal.FindFirst("sub")!.Value);
        Assert.Equal("fake-name", stored.Principal.FindFirst("unique_name")!.Value);
        Assert.True(stored.Principal.IsInRole("admin"));

        // The opaque session cookie authenticates the protected admin API.
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        apiRequest.Headers.Add("Cookie", cookieValue);
        using var apiResponse = await client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);

        var surfaces = string.Join('\n', response.Headers) + await response.Content.ReadAsStringAsync() + response.Headers.Location + string.Join('\n', logs.Messages);
        foreach (var canary in new[] { OidcTestAuthority.Secret, authority.LastAccessToken!, authority.LastVerifier!, authority.LastIdToken!, code, handshake.Query["state"] })
            Assert.DoesNotContain(canary, surfaces);

        Failed(await Callback(client, handshake, code));
        Assert.Equal(1, authority.Redeems);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("none")]
    public async Task NonAdminAccessTokenIsDeniedWithoutSession(string accessDefect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var response = await Callback(client, handshake, authority.Code(handshake.Query, accessDefect: accessDefect));
        Failed(response, AdminOidcRegistration.DeniedRedirect);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Theory]
    [InlineData("unsigned")][InlineData("signature")][InlineData("kid")][InlineData("typ")][InlineData("alg")]
    [InlineData("issuer")][InlineData("aud")][InlineData("aud-array")][InlineData("aud-single-array")]
    [InlineData("sub-missing")][InlineData("sub-empty")][InlineData("sub-array")][InlineData("sub-duplicate")]
    [InlineData("sub-number")][InlineData("iss-array")][InlineData("iss-number")][InlineData("iss-empty")][InlineData("iss-duplicate")]
    [InlineData("iat-missing")][InlineData("iat-future")][InlineData("iat-string")]
    [InlineData("exp-before-iat")][InlineData("exp-expired")][InlineData("nonce")][InlineData("nonce-missing")]
    public async Task InvalidIdTokenIsRejectedWithoutTicket(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query, defect)));
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Theory]
    [InlineData("sub-mismatch")][InlineData("sub-missing")][InlineData("sub-empty")][InlineData("sub-number")][InlineData("sub-array")][InlineData("sub-duplicate")]
    [InlineData("iss-array")][InlineData("iss-number")][InlineData("iss-empty")][InlineData("iss-duplicate")]
    [InlineData("access-expired")][InlineData("access-typ")][InlineData("access-aud")][InlineData("access-issuer")][InlineData("access-signature")]
    public async Task InvalidAccessTokenIsRejectedWithoutTicket(string accessDefect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query, accessDefect: accessDefect)));
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Theory]
    [InlineData("state-missing")][InlineData("state-duplicate")][InlineData("state-wrong")]
    [InlineData("iss-missing")][InlineData("iss-duplicate")][InlineData("iss-wrong")]
    [InlineData("code-missing")][InlineData("code-duplicate")][InlineData("code-error")]
    [InlineData("correlation-missing")][InlineData("nonce-cookie-missing")]
    public async Task InvalidCallbackCannotBypassStateIssuerOrCookieBindings(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        var parameters = new Dictionary<string, string?> { ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer, ["code"] = code };
        if (defect.EndsWith("-missing") && !defect.Contains("cookie") && !defect.Contains("correlation")) parameters.Remove(defect.Split('-')[0]);
        if (defect == "state-wrong") parameters["state"] = new string('x', 43);
        if (defect == "iss-wrong") parameters["iss"] = "https://wrong.example.test";
        if (defect == "code-error") parameters["error"] = "access_denied";
        var query = QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, parameters);
        if (defect.EndsWith("-duplicate")) { var key = defect.Split('-')[0]; query += "&" + key + "=" + Uri.EscapeDataString(parameters[key]!); }
        var cookies = handshake.Cookies;
        if (defect == "correlation-missing") cookies = string.Join("; ", cookies.Split("; ").Where(value => !value.Contains("Correlation")));
        if (defect == "nonce-cookie-missing") cookies = string.Join("; ", cookies.Split("; ").Where(value => !value.Contains("Nonce")));
        Failed(await Callback(client, handshake, code, query, cookies));
        Assert.Equal(defect == "nonce-cookie-missing" ? 1 : 0, authority.Redeems);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Fact]
    public async Task CancelledAnswersFixedResultAndConsumesPendingOnce()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var query = QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, new Dictionary<string, string?>
        {
            ["error"] = "access_denied", ["error_description"] = "fictitious-error-canary", ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer
        });
        Failed(await Callback(client, handshake, null, query), AdminOidcRegistration.CancelledRedirect);
        Failed(await Callback(client, handshake, null, query));
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Theory]
    [InlineData("500")][InlineData("json")][InlineData("issuer")][InlineData("timeout")][InlineData("jwks")]
    public async Task BadDiscoveryStartAnswersFixedFailureWithoutPending(string defect)
    {
        using var authority = new OidcTestAuthority { DiscoveryDefect = defect };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var response = await client.GetAsync("/admin/auth/oidc/start");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(AdminOidcEndpoints.IdentityUnavailableRedirect, response.Headers.Location!.OriginalString);
        Assert.Equal(0, factory.Services.GetRequiredService<CompactStateDataFormat>().Count);
        Assert.Equal(0, authority.Redeems);
    }

    [Theory]
    [InlineData("500")][InlineData("json")][InlineData("timeout")]
    public async Task BadTokenResponsePublishesNoTicketAndNeverRetriesCode(string failure)
    {
        using var authority = new OidcTestAuthority { TokenFailure = failure };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        Failed(await Callback(client, handshake, code));
        Failed(await Callback(client, handshake, code));
        Assert.Equal(1, authority.Redeems);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Theory]
    [InlineData("//external.example")]
    [InlineData("/\\external.example")]
    [InlineData("https://external.example")]
    [InlineData("http://127.0.0.1:5012/admin")]
    [InlineData("/%2f%2fexternal.example")]
    [InlineData("/%255cexternal.example")]
    [InlineData("/admin/auth/oidc/start")]
    [InlineData("/safe%0d%0aLocation:external")]
    public async Task InvalidReturnUrlAnswersFixed400WithoutPending(string returnUrl)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var response = await client.GetAsync("/admin/auth/oidc/start?returnUrl=" + Uri.EscapeDataString(returnUrl));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("inside this site", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Services.GetRequiredService<CompactStateDataFormat>().Count);
        Assert.Equal(0, authority.Redeems);
    }

    [Fact]
    public async Task MissingReturnUrlDefaultsToSpaRoot()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client, "/");
        var code = authority.Code(handshake.Query);
        using var response = await Callback(client, handshake, code);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ParallelRepeatRedeemsOnceAndIndependentHandshakesBothSucceed()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        // Two concurrent callbacks over the same pending state: exactly one completes the
        // exchange (the other is answered by the destroyed pending, and HttpClient's
        // idempotent-GET retry of the winner is answered the same way).
        var responses = await Task.WhenAll(Callback(client, handshake, code), Callback(client, handshake, code));
        Assert.Single(responses, response => response.Headers.Location!.OriginalString == "/admin/document-files");
        Assert.Equal(1, authority.Redeems);
        var starts = await Task.WhenAll(Start(client, "/admin/one"), Start(client, "/admin/two"));
        Assert.NotEqual(starts[0].Query["state"], starts[1].Query["state"]);
        Assert.NotEqual(starts[0].Cookies, starts[1].Cookies);
        // Independent handshakes each redeem their own code (sequential here: the concurrent
        // one-time consumption contract is covered above and by the store unit tests).
        var first = await Callback(client, starts[0], authority.Code(starts[0].Query));
        var second = await Callback(client, starts[1], authority.Code(starts[1].Query));
        responses = [first, second];
        Assert.Equal(new[] { "/admin/one", "/admin/two" }, responses.Select(response => response.Headers.Location!.OriginalString).Order().ToArray());
        Assert.Equal(3, authority.Redeems);
    }

    [Fact]
    public async Task DisabledByDefaultKeepsLegacyContractAndAnswersFixed503()
    {
        var root = CreateTempContentRoot(true);
        try
        {
            using var factory = CreateFactory(root);
            using var client = Browser(factory);
            foreach (var path in new[] { "/admin/auth/oidc/start", AdminOidcConstants.CallbackPath, "/admin/auth/oidc/csrf", "/admin/auth/oidc/session", AdminOidcConstants.LogoutReturnPath })
            {
                var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("{\"success\":false,\"message\":\"Hosted sign-in is not enabled.\"}", await response.Content.ReadAsStringAsync());
            }
            var disabledLogout = await client.PostAsync("/admin/auth/oidc/logout", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledLogout.StatusCode);
            foreach (var path in new[] { "/", "/health/live" }) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ProtectedApiRoute)).StatusCode);
            // The legacy auth area keeps answering with its own contract (not the fixed 503):
            // logout unconditionally succeeds and the session probe still challenges.
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/admin/auth/logout", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/admin/auth/session")).StatusCode);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionWritesRequireCsrfAndBearerCallersAreUnaffected()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var cookieValue = SessionCookie(await Callback(client, handshake, authority.Code(handshake.Query)));

        // Safe method: the session reaches the protected API.
        using var get = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        get.Headers.Add("Cookie", cookieValue);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(get)).StatusCode);

        // Non-safe method without the CSRF credential: fixed 403, endpoint never runs.
        using var barePost = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        barePost.Headers.Add("Cookie", cookieValue);
        var bare = await client.SendAsync(barePost);
        Assert.Equal(HttpStatusCode.Forbidden, bare.StatusCode);
        Assert.Contains("anti-forgery", await bare.Content.ReadAsStringAsync());

        // Fetch the CSRF credential pair with the live session.
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/csrf");
        csrfRequest.Headers.Add("Cookie", cookieValue);
        using var csrfResponse = await client.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        var csrfCookie = string.Join("; ", csrfResponse.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        var requestToken = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("requestToken").GetString()!;

        // With the CSRF credential pair the boundary passes; the request then reaches route
        // matching, which answers 405 because the list endpoint has no POST handler — proof
        // the write crossed the CSRF boundary without being answered by it.
        using var post = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        post.Headers.Add("Cookie", cookieValue + "; " + csrfCookie);
        post.Headers.Add(AdminOidcConstants.CsrfHeader, requestToken);
        var posted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);

        // A wrong CSRF header value is still rejected.
        using var wrongPost = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        wrongPost.Headers.Add("Cookie", cookieValue + "; " + csrfCookie);
        wrongPost.Headers.Add(AdminOidcConstants.CsrfHeader, "forged-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongPost)).StatusCode);

        // Bearer callers keep the legacy behavior: no CSRF requirement, same acceptance. The
        // token comes from the suite's static trust anchor, unrelated to the fake SignaCore.
        using var bearer = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        bearer.Headers.Add("Authorization", "Bearer " + CreateToken("admin"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);

        // Transition invariant until the password flow is retired: the legacy access-token
        // cookie still authenticates under the JwtBearer fallback, distinct cookie names
        // keep the two schemes from reading each other's cookie.
        using var legacyCookie = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        legacyCookie.Headers.Add("Cookie", "docthecaAccessToken=" + CreateToken("admin"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(legacyCookie)).StatusCode);
    }

    [Fact]
    public async Task ExpiredSessionAnswersFixedReauthenticationWithoutRefreshOrReplay()
    {
        using var authority = new OidcTestAuthority();
        var time = new ManualOidcTime();
        using var factory = OidcFactory(authority, time);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var cookieValue = SessionCookie(await Callback(client, handshake, authority.Code(handshake.Query)));
        using var get = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        get.Headers.Add("Cookie", cookieValue);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(get)).StatusCode);

        // Advance past the access token expiry (900 s) plus clock skew: the session is gone.
        time.Advance(TimeSpan.FromMinutes(16));
        using var expiredGet = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        expiredGet.Headers.Add("Cookie", cookieValue);
        var expired = await client.SendAsync(expiredGet);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal("{\"success\":false,\"message\":\"Re-authentication is required.\"}", await expired.Content.ReadAsStringAsync());

        // No automatic refresh: the fake authority saw exactly the one token redemption, and
        // a non-safe request is answered the same way instead of being replayed.
        using var expiredPost = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        expiredPost.Headers.Add("Cookie", cookieValue);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(expiredPost)).StatusCode);
        Assert.Equal(1, authority.Redeems);
    }

    [Fact]
    public async Task StartCancellationCreatesNoPendingOrTicket()
    {
        using var authority = new OidcTestAuthority { HoldDiscovery = true };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAsync("/admin/auth/oidc/start", cancellation.Token);
        await authority.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, factory.Services.GetRequiredService<CompactStateDataFormat>().Count);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
    }

    [Fact]
    public async Task RestartLosesInProcessServerTickets()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var cookieValue = SessionCookie(await Callback(client, handshake, authority.Code(handshake.Query)));
        using var get = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        get.Headers.Add("Cookie", cookieValue);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(get)).StatusCode);

        // A different host process cannot resolve the cookie: the declared first-stage
        // limitation is that sessions live only in the process that issued them.
        using var restarted = OidcFactory(authority);
        using var restartedClient = Browser(restarted);
        using var expired = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        expired.Headers.Add("Cookie", cookieValue);
        var response = await restartedClient.SendAsync(expired);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Re-authentication is required.", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, restarted.Services.GetRequiredService<MemoryTicketStore>().Count);
    }
}

internal sealed class OidcLogCapture : ILoggerProvider
{
    internal ConcurrentQueue<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose() { }

    private sealed class Logger(OidcLogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Messages.Enqueue(formatter(state, exception) + exception?.ToString());
    }
}
