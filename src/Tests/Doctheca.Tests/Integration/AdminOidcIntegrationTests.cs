using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
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
/// End-to-end hosted-login tests against the real Program.cs with an in-process fake authority
/// (see <see cref="OidcTestAuthority"/>). The protocol runs on the framework's own OpenID Connect
/// handler; these tests pin the externally visible contract of the replaced package integration:
/// the full code flow, the admin gate, state/iss/correlation failures, code replay, upstream
/// unavailability, cancellation, invalid return URLs, the missing-configuration contract, the CSRF
/// boundary, and the fixed re-authentication result after the access token expires.
/// </summary>
[Collection(ServiceMantleIntegrationCollection.Name)]
public sealed partial class AdminOidcIntegrationTests(PostgreSqlFixture database) : ServiceMantleIntegrationTestBase(database)
{
    private WebApplicationFactory<Program> OidcFactory(OidcTestAuthority authority, ManualOidcTime? time = null,
        Action<IServiceCollection>? configure = null, bool http = false)
        => CreateFactory(configureTestServices: services =>
        {
            // The OpenID Connect backchannel is the named client; the short timeout keeps held
            // responses fail closed within the test budgets.
            services.AddHttpClient(AdminOidcConstants.HttpClientName)
                .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(2))
                .ConfigurePrimaryHttpMessageHandler(() => authority);
            if (time is not null) services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time));
            configure?.Invoke(services);
        }, settings: new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "false", ["AdminOidc:PostLogoutRedirectUri"] = http ? OidcTestAuthority.PostLogoutUri.Replace("https:", "http:") : OidcTestAuthority.PostLogoutUri,
            ["AdminOidc:RedirectUri"] = http ? OidcTestAuthority.RedirectUri.Replace("https:", "http:") : OidcTestAuthority.RedirectUri,
            ["IdentityService:Authority"] = OidcTestAuthority.Issuer,
            ["IdentityService:Issuer"] = OidcTestAuthority.Issuer,
            // Mixed-session tests use the base fixture's separately signed Bearer token.
            ["IdentityService:AdditionalValidIssuers:1"] = TestIssuer,
            ["IdentityService:AppId"] = OidcTestAuthority.ClientId,
            ["IdentityService:AppSecret"] = OidcTestAuthority.Secret
        }, environment: http ? "Production" : "Testing");

    private static HttpClient Browser(WebApplicationFactory<Program> factory, bool http = false) => factory.CreateClient(new()
    {
        BaseAddress = new Uri(http ? "http://admin.example.test" : "https://admin.example.test"), AllowAutoRedirect = false, HandleCookies = false
    });

    /// <summary>
    /// The browser-held state of one started sign-in: the authorize-redirect query plus every
    /// carrier cookie the challenge set (the framework's correlation and nonce cookies, both
    /// HttpOnly and single-purpose like the package's binding cookie was). Manual cookies keep
    /// tests able to exercise missing/wrong bindings too.
    /// </summary>
    private sealed record Handshake(Dictionary<string, string> Query, HttpResponseMessage Response, string[] Cookies);

    private static async Task<Handshake> Start(HttpClient client, string target = "/admin/document-files")
    {
        var response = await client.GetAsync("/admin/auth/oidc/start?returnUrl=" + Uri.EscapeDataString(target));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(OidcTestAuthority.Issuer + "/authorize", response.Headers.Location!.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        var cookies = response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]).ToArray();
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.Correlation."));
        Assert.Contains(cookies, cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce."));
        foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
        {
            Assert.Contains("httponly", cookie.ToLowerInvariant());
            Assert.Equal(query["redirect_uri"].StartsWith("https:"), cookie.Split(';').Any(value => value.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase)));
        }
        return new(query, response, cookies);
    }

    private static string CorrelationCookie(Handshake handshake) =>
        Assert.Single(handshake.Cookies, cookie => cookie.StartsWith(".AspNetCore.Correlation."));

    private static string NonceCookie(Handshake handshake) =>
        Assert.Single(handshake.Cookies, cookie => cookie.StartsWith(".AspNetCore.OpenIdConnect.Nonce."));

    private static Task<HttpResponseMessage> Callback(HttpClient client, Handshake handshake, string? code, string? query = null,
        string? cookies = null, CancellationToken cancellation = default, bool includeBinding = true)
    {
        query ??= QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, new Dictionary<string, string?>
        {
            ["code"] = code, ["state"] = handshake.Query["state"], ["iss"] = OidcTestAuthority.Issuer
        });
        var request = new HttpRequestMessage(HttpMethod.Get, query);
        if (includeBinding) cookies = string.IsNullOrEmpty(cookies)
            ? string.Join("; ", handshake.Cookies) : cookies + "; " + string.Join("; ", handshake.Cookies);
        if (!string.IsNullOrEmpty(cookies)) request.Headers.Add("Cookie", cookies);
        return client.SendAsync(request, cancellation);
    }

    [Theory]
    [InlineData("missing")][InlineData("wrong")][InlineData("other-sign-in")]
    public async Task CallbackRequiresOriginalBrowserBinding(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        string? cookies = defect switch
        {
            // No correlation cookie at all: the response cannot be tied to this browser.
            "missing" => NonceCookie(handshake),
            // A forged value never matches the single-purpose correlation marker.
            "wrong" => CorrelationCookie(handshake).Split('=')[0] + "=" + new string('x', 43)
                + "; " + NonceCookie(handshake),
            // Another sign-in's correlation cookie names a different pending handshake.
            _ => CorrelationCookie(await Start(client, "/")) + "; " + NonceCookie(handshake)
        };
        Failed(await Callback(client, handshake, code, cookies: cookies, includeBinding: false));
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
    }

    private static void Failed(HttpResponseMessage response, string target = AdminLoginResponseWriter.SignInFailedRedirect)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(target, response.Headers.Location!.OriginalString);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var values) && values.Any(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")));
    }

    private static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith(AdminOidcConstants.SessionCookie + "=")).Split(';')[0];

    private static AdminOidcTicketStore Tickets(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<AdminOidcTicketStore>();

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
        Assert.Equal(OidcTestAuthority.RedirectUri, handshake.Query["redirect_uri"]);
        Assert.NotEmpty(handshake.Query["state"]);
        Assert.NotEmpty(handshake.Query["nonce"]);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", handshake.Query["code_challenge"]);
        Assert.Equal("S256", handshake.Query["code_challenge_method"]);

        var code = authority.Code(handshake.Query);
        using var response = await Callback(client, handshake, code);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/document-files", response.Headers.Location!.OriginalString);
        var form = Assert.Single(authority.TokenForms);
        // The generic handler authenticates the token request with the client secret in the form
        // (client_secret_post) — the authority accepts it exactly like it accepted the package's
        // client_secret_basic; the secret still never reaches the browser.
        Assert.Equal(new[] { "client_id", "client_secret", "code", "code_verifier", "grant_type", "redirect_uri" }, form.Keys.Order().ToArray());
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(OidcTestAuthority.RedirectUri, form["redirect_uri"]);
        Assert.Null(Assert.Single(authority.AuthorizationHeaders));
        Assert.Equal(OidcTestAuthority.Secret, form["client_secret"]);

        // The cookie holds a data-protected reference to the server-side ticket; the ticket itself
        // keeps both tokens server-side for the session's whole life.
        var cookieValue = SessionCookie(response);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AdminOidcConstants.SessionCookie + "="));
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("secure", cookie.ToLowerInvariant());
        Assert.Contains("samesite=lax", cookie.ToLowerInvariant());
        Assert.Contains("path=/", cookie.ToLowerInvariant());
        Assert.Contains("expires=", cookie.ToLowerInvariant());
        var stored = Assert.Single(Tickets(factory).Snapshot());
        Assert.Equal(authority.LastAccessToken, stored.Properties.GetTokenValue("access_token"));
        Assert.Equal(authority.LastIdToken, stored.Properties.GetTokenValue("id_token"));
        Assert.True(stored.Properties.ExpiresUtc > DateTimeOffset.UtcNow);
        Assert.Equal("fake-subject", stored.Principal.FindFirst("sub")!.Value);
        Assert.Equal("fake-name", stored.Principal.FindFirst("name")!.Value);
        Assert.True(stored.Principal.IsInRole("admin"));

        // The single-use carrier cookies of the handshake are finished on the same paths.
        foreach (var name in handshake.Cookies.Select(value => value.Split('=')[0]))
        {
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
                value.StartsWith(name + "=;")
                && value.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        }

        // The opaque session cookie authenticates the protected admin API.
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        apiRequest.Headers.Add("Cookie", cookieValue);
        using var apiResponse = await client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.OK, apiResponse.StatusCode);

        var surfaces = string.Join('\n', response.Headers) + await response.Content.ReadAsStringAsync() + response.Headers.Location + string.Join('\n', logs.Messages);
        foreach (var canary in new[] { OidcTestAuthority.Secret, authority.LastAccessToken!, authority.LastVerifier!, authority.LastIdToken!, code })
            Assert.DoesNotContain(canary, surfaces);
        Assert.DoesNotContain(handshake.Query["state"],
            string.Join('\n', response.Headers.Where(header => header.Key != "Set-Cookie"))
            + await response.Content.ReadAsStringAsync() + string.Join('\n', logs.Messages));

        // A replayed callback cannot redeem the one-time authorization code twice: the authority
        // refuses the second use and no second session ever exists.
        Assert.Equal(HttpStatusCode.Redirect, (await Callback(client, handshake, code)).StatusCode);
        Assert.Equal(2, authority.Redeems);
        Assert.Equal(1, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("id-user")]
    [InlineData("id-none")]
    [InlineData("id-user", true)]
    public async Task NonAdminIdentityIsDeniedWithoutSession(string idDefect, bool http = false)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
        var handshake = await Start(client);
        var response = await Callback(client, handshake, authority.Code(handshake.Query, idDefect));
        Failed(response, AdminLoginResponseWriter.DeniedRedirect);
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("unsigned")][InlineData("signature")][InlineData("alg")]
    [InlineData("issuer")][InlineData("aud")]
    [InlineData("sub-missing")][InlineData("sub-empty")]
    [InlineData("iss-empty")]
    [InlineData("exp-expired")][InlineData("nonce")][InlineData("nonce-missing")]
    [InlineData("nonce", true)]
    public async Task InvalidIdTokenIsRejectedWithoutTicket(string defect, bool http = false)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
        var handshake = await Start(client);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query, defect)));
        Assert.Equal(0, Tickets(factory).Count);
    }

    /// <summary>
    /// Standard OIDC validation semantics of the framework handler (the replaced package applied
    /// them too): a multi-valued <c>aud</c> containing the client id is valid, no <c>iat</c> claim
    /// is demanded, an expiry is not cross-checked against <c>iat</c>, an unknown <c>kid</c> falls
    /// back to the whole published JWKS, and duplicate flattened claims take their last value. The
    /// ID token's <c>typ</c> header is not interpreted by the framework.
    /// </summary>
    [Theory]
    [InlineData("aud-array")][InlineData("aud-single-array")]
    [InlineData("iat-missing")][InlineData("iat-string")][InlineData("iat-near-future")]
    [InlineData("exp-before-iat")][InlineData("iss-duplicate")][InlineData("kid")]
    [InlineData("typ")]
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
        if (defect == "state-wrong") parameters["state"] = "tampered-state";
        if (defect == "iss-wrong") parameters["iss"] = "https://wrong.example.test";
        var query = QueryHelpers.AddQueryString(AdminOidcConstants.CallbackPath, parameters);
        if (defect.EndsWith("-duplicate")) { var key = defect.Split('-')[0]; query += "&" + key + "=" + Uri.EscapeDataString(parameters[key]!); }
        Failed(await Callback(client, handshake, code, query));
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
    }

    /// <summary>
    /// The package separated cancellation from gate denial; the host keeps both on the existing
    /// notAdmin result. Repeated cancellation still creates no ticket or exchange.
    /// </summary>
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelledAnswersDeniedResultWithoutTicketOrExchange(bool http)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
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
    /// Declared mapping: a callback whose token endpoint is unreachable surfaces as the
    /// identity-unavailable outcome, not a generic sign-in failure; a refused (error status) or
    /// unparseable token response is a sign-in failure. A browser replay of the callback (whose
    /// carrier cookies were consumed) never re-attempts the exchange.
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
        // The browser's carrier cookies were consumed by the failed callback: a replay of the
        // navigation cannot redeem the code again.
        Failed(await Callback(client, handshake, code, includeBinding: false));
        Assert.Equal(1, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("scope")][InlineData("duplicate")][InlineData("oversized")]
    public async Task FrameworkToleratedTokenResponsesCompleteSignIn(string padding)
    {
        using var authority = new OidcTestAuthority { TokenFailure = padding };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        using var response = await Callback(client, handshake, authority.Code(handshake.Query));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin/document-files", response.Headers.Location!.OriginalString);
        Assert.Equal(1, Tickets(factory).Count);
    }

    [Theory]
    [InlineData("500")][InlineData("json")][InlineData("timeout")][InlineData("jwks")]
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

    /// <summary>
    /// A discovery document with a wrong issuer value is fetched successfully, so the sign-in
    /// start still redirects to the authority; the issuer mismatch surfaces at the callback as
    /// the fixed sign-in failure (RFC 9207 check), before any code is exchanged.
    /// </summary>
    [Fact]
    public async Task WrongDiscoveryIssuerSurfacesAtCallbackAsSignInFailure()
    {
        using var authority = new OidcTestAuthority { DiscoveryDefect = "issuer" };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var handshake = await Start(client);
        Assert.Equal(HttpStatusCode.Redirect, handshake.Response.StatusCode);
        Failed(await Callback(client, handshake, authority.Code(handshake.Query)));
        Assert.Equal(0, authority.Redeems);
        Assert.Equal(0, Tickets(factory).Count);
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

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task ParallelRepeatRedeemsOnceAndIndependentHandshakesBothSucceed(bool http)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        // Two concurrent callbacks over the same pending state: exactly one completes the
        // exchange; the other's redemption is refused by the one-time authorization code itself.
        var responses = await Task.WhenAll(Callback(client, handshake, code), Callback(client, handshake, code));
        Assert.Single(responses, response => response.Headers.Location!.OriginalString == "/admin/document-files");
        Assert.Single(responses, response => response.Headers.Location!.OriginalString == AdminLoginResponseWriter.SignInFailedRedirect);
        Assert.Equal(2, authority.Redeems);
        Assert.Equal(1, Tickets(factory).Count);
        var starts = await Task.WhenAll(Start(client, "/admin/one"), Start(client, "/admin/two"));
        Assert.NotEqual(starts[0].Query["state"], starts[1].Query["state"]);
        // Independent handshakes each redeem their own code.
        var first = await Callback(client, starts[0], authority.Code(starts[0].Query));
        var second = await Callback(client, starts[1], authority.Code(starts[1].Query));
        responses = [first, second];
        Assert.Equal(new[] { "/admin/one", "/admin/two" }, responses.Select(response => response.Headers.Location!.OriginalString).Order().ToArray());
        Assert.Equal(4, authority.Redeems);
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

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task SessionWritesRequireCsrfAndBearerCallersAreUnaffected(bool http)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
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

        // Fetch the CSRF credential pair with the live session. The declared shape is kept: the
        // csrf endpoint answers {"token": "..."} directly.
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/csrf");
        csrfRequest.Headers.Add("Cookie", cookieValue);
        using var csrfResponse = await client.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
        var csrfCookie = string.Join("; ", csrfResponse.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        Assert.Contains(AdminOidcConstants.CsrfCookie + "=", csrfCookie);
        var requestToken = (await csrfResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        // A valid CSRF pair now reaches the additive book creation handler (issue #133).
        using var post = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        post.Headers.Add("Cookie", cookieValue + "; " + csrfCookie);
        post.Headers.Add(AdminOidcConstants.CsrfHeader, requestToken);
        post.Content = JsonContent.Create(new { fileName = "CSRF book " + Guid.NewGuid() });
        var posted = await client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);

        // A wrong CSRF header value is still rejected.
        using var wrongPost = new HttpRequestMessage(HttpMethod.Post, ProtectedApiRoute);
        wrongPost.Headers.Add("Cookie", cookieValue + "; " + csrfCookie);
        wrongPost.Headers.Add(AdminOidcConstants.CsrfHeader, "forged-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongPost)).StatusCode);

        // Bearer callers keep the legacy behavior: no CSRF requirement, same acceptance. The
        // token comes from the suite's static trust anchor, unrelated to the fake authority.
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
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task StartCancellationCreatesNoPendingOrTicket(bool http)
    {
        using var authority = new OidcTestAuthority { HoldDiscovery = true };
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
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
    /// A rejected callback (an invalid ID token here) never touches an existing live session: the
    /// old ticket stays, and nothing of the failed handshake leaks into responses or logs.
    /// </summary>
    [Fact]
    public async Task RejectedCallbackDoesNotReplaceOrExtendExistingSessionAndDoesNotLeak()
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
        var signIn = await Start(client);
        var session = SessionCookie(await Callback(client, signIn, authority.Code(signIn.Query)));
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query, "nonce");
        var response = await Callback(client, handshake, code, cookies: session);
        Failed(response, AdminLoginResponseWriter.SignInFailedRedirect);
        using var original = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/session");
        original.Headers.Add("Cookie", session);
        var status = await client.SendAsync(original);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(1, Tickets(factory).Count);
        var surfaces = await response.Content.ReadAsStringAsync() + string.Join('\n', response.Headers) + response.Headers.Location
            + await status.Content.ReadAsStringAsync() + string.Join('\n', logs.Messages);
        foreach (var canary in new[] { OidcTestAuthority.Secret, authority.LastAccessToken!, authority.LastIdToken!, authority.LastVerifier!, code })
            Assert.DoesNotContain(canary, surfaces);
        Assert.DoesNotContain("fictitious-code-", surfaces);
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
