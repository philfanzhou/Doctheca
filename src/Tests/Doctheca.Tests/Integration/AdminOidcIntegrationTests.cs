using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Doctheca.Host.Authentication;
using Doctheca.Tests.Authentication;
using SignaCore.Client.AspNetCore;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// End-to-end hosted-login tests against the real Program.cs with an in-process fake SignaCore
/// (see <see cref="OidcTestAuthority"/>). Since issue #70 the protocol runs in the official
/// SignaCore.Client.AspNetCore package; these tests pin the externally visible contract of the
/// replaced self-written slice: the full code flow, the admin gate, state/iss/nonce failures,
/// code replay, upstream unavailability, cancellation, invalid return URLs, the
/// missing-configuration contract, the CSRF boundary, and the fixed re-authentication result
/// after the access token expires.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class AdminOidcIntegrationTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private WebApplicationFactory<Program> OidcFactory(OidcTestAuthority authority, ManualOidcTime? time = null,
        Action<IServiceCollection>? configure = null)
        => CreateFactory(configureTestServices: services =>
        {
            // The package consumes the authority through its named backchannel client; the short
            // timeout keeps held responses fail closed within the test budgets.
            services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2))
                .ConfigurePrimaryHttpMessageHandler(() => authority);
            if (time is not null) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
            configure?.Invoke(services);
        }, settings: new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "false", ["AdminOidc:PostLogoutRedirectUri"] = OidcTestAuthority.PostLogoutUri,
            ["AdminOidc:RedirectUri"] = OidcTestAuthority.RedirectUri,
            ["IdentityService:Authority"] = OidcTestAuthority.Issuer,
            ["IdentityService:Issuer"] = OidcTestAuthority.Issuer,
            // Mixed-session tests use the base fixture's separately signed Bearer token.
            ["IdentityService:AdditionalValidIssuers:1"] = TestIssuer,
            ["IdentityService:AppId"] = OidcTestAuthority.ClientId,
            ["IdentityService:AppSecret"] = OidcTestAuthority.Secret
        });

    private static HttpClient Browser(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://admin.example.test"), AllowAutoRedirect = false, HandleCookies = false
    });

    private sealed record Handshake(Dictionary<string, string> Query, HttpResponseMessage Response);

    private static async Task<Handshake> Start(HttpClient client, string target = "/admin/document-files")
    {
        var response = await client.GetAsync("/admin/auth/oidc/start?returnUrl=" + Uri.EscapeDataString(target));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(OidcTestAuthority.Issuer + "/authorize", response.Headers.Location!.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        // The pending sign-in lives entirely server-side: the start response carries no cookie.
        Assert.False(response.Headers.Contains("Set-Cookie"));
        return new(query, response);
    }

    private static Task<HttpResponseMessage> Callback(HttpClient client, Handshake handshake, string? code, string? query = null,
        string? cookies = null, CancellationToken cancellation = default)
    {
        query ??= QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, new Dictionary<string, string?>
        {
            ["code"] = code, ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer
        });
        var request = new HttpRequestMessage(HttpMethod.Get, query);
        if (!string.IsNullOrEmpty(cookies)) request.Headers.Add("Cookie", cookies);
        return client.SendAsync(request, cancellation);
    }

    private static void Failed(HttpResponseMessage response, string target = AdminLoginResponseWriter.SignInFailedRedirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target, response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")));
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")).Split(';')[0];

    private static InMemoryTicketStore Tickets(WebApplicationFactory<Program> factory)
        => (InMemoryTicketStore)factory.Services.GetRequiredService<ITicketStore>();

    private static string SessionKey(HttpResponseMessage response) => SessionCookie(response)[(AdminOidcConstants.SessionCookie.Length + 1)..];

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

        var code = authority.Code(handshake.Query);
        using var response = await Callback(client, handshake, code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/document-files", response.Headers.Location!.OriginalString);
        var form = Assert.Single(authority.TokenForms);
        // D1: the client credential travels as HTTP Basic authentication, never in the form.
        Assert.Equal(new[] { "code", "code_verifier", "grant_type", "redirect_uri" }, form.Keys.Order().ToArray());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(OidcTestAuthority.RedirectUri, form["redirect_uri"]);
        var basic = Assert.Single(authority.AuthorizationHeaders);
        Assert.StartsWith("Basic ", basic);
        Assert.Equal(OidcTestAuthority.ClientId + ":" + OidcTestAuthority.Secret,
            Encoding.UTF8.GetString(Convert.FromBase64String(basic!["Basic ".Length..])));

        // The cookie holds the raw store key of the server-side ticket; the ticket itself keeps
        // both verified tokens server-side for the session's whole life.
        var cookieValue = SessionCookie(response);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AdminOidcConstants.SessionCookie + "="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        var stored = await Tickets(factory).RetrieveAsync(SessionKey(response), CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(authority.LastAccessToken, stored!.AccessToken);
        Assert.Equal(authority.LastIdToken, stored.IdToken);
        Assert.True(stored.ExpiresUtc > DateTimeOffset.UtcNow);
        Assert.Equal("fake-subject", stored.Principal.FindFirst("sub")!.Value);
        Assert.Equal("fake-name", stored.Principal.FindFirst("name")!.Value);
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
        Failed(response, AdminLoginResponseWriter.DeniedRedirect);
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("unsigned")][InlineData("signature")][InlineData("typ")][InlineData("alg")]
    [InlineData("issuer")][InlineData("aud")]
    [InlineData("sub-missing")][InlineData("sub-empty")][InlineData("sub-array")][InlineData("sub-duplicate")]
    [InlineData("sub-number")][InlineData("iss-array")][InlineData("iss-number")][InlineData("iss-empty")]
    [InlineData("exp-expired")][InlineData("nonce")][InlineData("nonce-missing")]
    public async Task InvalidIdTokenIsRejectedWithoutTicket(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query, defect)));
        Assert.Equal(0, Tickets(factory).Count);
    }

    /// <summary>
    /// Declared relaxations of the replaced slice (issue #70): the official package applies the
    /// standard OIDC validation semantics — a multi-valued <c>aud</c> containing the client id is
    /// valid, no <c>iat</c> claim is demanded, an expiry within the 30-second clock skew is not
    /// rejected by comparing it to <c>iat</c>, and the signature is verified against the whole
    /// published JWKS instead of a single <c>kid</c>-selected key — while duplicate flattened
    /// claims and unknown keys keep failing through the strict forms asserted above.
    /// </summary>
    [Theory]
    [InlineData("aud-array")][InlineData("aud-single-array")]
    [InlineData("iat-missing")][InlineData("iat-future")][InlineData("iat-string")]
    [InlineData("exp-before-iat")][InlineData("iss-duplicate")][InlineData("kid")]
    public async Task StandardOidcIdTokenToleranceSignsIn(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        using var response = await Callback(client, handshake, authority.Code(handshake.Query, defect));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/document-files", response.Headers.Location!.OriginalString);
        Assert.Equal(1, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("sub-mismatch")][InlineData("sub-missing")][InlineData("sub-empty")][InlineData("sub-number")][InlineData("sub-array")][InlineData("sub-duplicate")]
    [InlineData("iss-array")][InlineData("iss-number")][InlineData("iss-empty")][InlineData("iss-duplicate")]
    [InlineData("access-expired")][InlineData("access-typ")][InlineData("access-aud")][InlineData("access-issuer")][InlineData("access-signature")]
    [InlineData("access-nbf-future")][InlineData("nbf-missing")]
    public async Task InvalidAccessTokenIsRejectedWithoutTicket(string accessDefect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query, accessDefect: accessDefect)));
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("state-missing")][InlineData("state-duplicate")][InlineData("state-wrong")]
    [InlineData("iss-missing")][InlineData("iss-duplicate")][InlineData("iss-wrong")]
    [InlineData("code-missing")][InlineData("code-duplicate")]
    public async Task InvalidCallbackCannotBypassStateOrIssuerBindings(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        var parameters = new Dictionary<string, string?> { ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer, ["code"] = code };
        if (defect.EndsWith("-missing")) parameters.Remove(defect.Split('-')[0]);
        if (defect == "state-wrong") parameters["state"] = new string('x', 43);
        if (defect == "iss-wrong") parameters["iss"] = "https://wrong.example.test";
        var query = QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, parameters);
        if (defect.EndsWith("-duplicate")) { var key = defect.Split('-')[0]; query += "&" + key + "=" + Uri.EscapeDataString(parameters[key]!); }
        Failed(await Callback(client, handshake, code, query));
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
    }

    /// <summary>
    /// Declared merge (issue #70 D2): the package reports the user's cancellation and a gate
    /// denial with the same bounded reason, so both surface as the merged notAdmin outcome. The
    /// pending state stays single-use: the error answer consumes it before anything else.
    /// </summary>
    [Fact]
    public async Task CancelledAnswersMergedDeniedResultAndConsumesPendingOnce()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var query = QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, new Dictionary<string, string?>
        {
            ["error"] = "access_denied", ["error_description"] = "fictitious-error-canary", ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer
        });
        Failed(await Callback(client, handshake, null, query), AdminLoginResponseWriter.DeniedRedirect);
        Failed(await Callback(client, handshake, null, query), AdminLoginResponseWriter.DeniedRedirect);
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
    }

    /// <summary>
    /// Declared mapping (issue #70 D2): a callback whose token endpoint is unreachable surfaces
    /// as the identity-unavailable outcome, not a generic sign-in failure.
    /// </summary>
    [Theory]
    [InlineData("500", AdminLoginResponseWriter.SignInFailedRedirect)]
    [InlineData("json", AdminLoginResponseWriter.SignInFailedRedirect)]
    [InlineData("timeout", AdminLoginResponseWriter.IdentityUnavailableRedirect)]
    public async Task BadTokenResponsePublishesNoTicketAndNeverRetriesCode(string failure, string target)
    {
        using var authority = new OidcTestAuthority { TokenFailure = failure };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        Failed(await Callback(client, handshake, code), target);
        Failed(await Callback(client, handshake, code));
        Assert.Equal(1, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
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
        Assert.Equal(AdminLoginResponseWriter.IdentityUnavailableRedirect, response.Headers.Location!.OriginalString);
        Assert.Equal(0, authority.Redeems);
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
        // Independent handshakes each redeem their own code (sequential here: the concurrent
        // one-time consumption contract is covered above and by the package's stores).
        var first = await Callback(client, starts[0], authority.Code(starts[0].Query));
        var second = await Callback(client, starts[1], authority.Code(starts[1].Query));
        responses = [first, second];
        Assert.Equal(new[] { "/admin/one", "/admin/two" }, responses.Select(response => response.Headers.Location!.OriginalString).Order().ToArray());
        Assert.Equal(3, authority.Redeems);
    }

    [Fact]
    public async Task MissingConfigurationStartsAndAnswersFixed503()
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
                Assert.Equal("{\"success\":false,\"message\":\"Hosted sign-in is not configured.\"}", await response.Content.ReadAsStringAsync());
            }
            var disabledLogout = await client.PostAsync("/admin/auth/oidc/logout", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, disabledLogout.StatusCode);
            foreach (var path in new[] { "/", "/health/live" }) Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ProtectedApiRoute)).StatusCode);
            // Retired POST and read-only projection retain their separate fixed contracts.
            Assert.Equal(HttpStatusCode.Gone, (await client.PostAsync("/admin/auth/logout", null)).StatusCode);
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

        // Fetch the CSRF credential pair with the live session. Declared shape change (issue
        // #70 D3): the package's csrf endpoint answers {"token": "..."} directly.
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/csrf");
        csrfRequest.Headers.Add("Cookie", cookieValue);
        using var csrfResponse = await client.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        var csrfCookie = string.Join("; ", csrfResponse.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        Assert.Contains(AdminOidcConstants.CsrfCookie + "=", csrfCookie);
        var requestToken = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

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

        // Old browser tokens cannot establish identity even when correctly signed.
        using var legacyCookie = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        legacyCookie.Headers.Add("Cookie", "docthecaAccessToken=" + CreateToken("admin"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(legacyCookie)).StatusCode);
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

        // Advance past the access token expiry (900 s): the session is gone.
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
        Assert.Equal(0, Tickets(factory).Count);
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
        Assert.Equal(0, Tickets(restarted).Count);
    }

    /// <summary>
    /// The pre-sign-in gate fails closed on decision timeout (issue #70 T4): a decision that
    /// never completes within the configured budget signs nobody in and changes nothing.
    /// </summary>
    [Fact]
    public async Task GateTimeoutDeniesSignInWithoutTicket()
    {
        using var authority = new OidcTestAuthority();
        var gate = new HeldPreSignInDecision();
        using var factory = OidcFactory(authority, configure: services =>
            services.Configure<SignaCoreHostedLoginOptions>(options =>
            { options.PreSignInAuthorizationDecision = gate; options.PreSignInAuthorizationTimeout = TimeSpan.FromMilliseconds(200); }));
        using var client = Browser(factory);
        var handshake = await Start(client);
        var response = await Callback(client, handshake, authority.Code(handshake.Query));
        Failed(response, AdminLoginResponseWriter.DeniedRedirect);
        Assert.Equal(0, Tickets(factory).Count);
        gate.Release();
    }

    private sealed class HeldPreSignInDecision : ISignaCorePreSignInAuthorizationDecision
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<SignaCoreAuthorizationDecisionResult> DecideAsync(
            SignaCorePreSignInAuthorizationContext context, CancellationToken cancellationToken)
            => new(_release.Task.WaitAsync(cancellationToken).ContinueWith(
                _ => SignaCoreAuthorizationDecisionResult.Allowed, CancellationToken.None));
        public void Release() => _release.TrySetResult();
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
