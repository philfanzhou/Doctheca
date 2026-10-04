using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Doctheca.Host.Authentication;
using Doctheca.Tests.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminOidcIntegrationTests
{
    private sealed record SessionCredentials(string Cookie, string CsrfCookie, string CsrfToken);

    private static async Task<SessionCredentials> SignInWithCsrf(HttpClient client, OidcTestAuthority authority)
    {
        var handshake = await Start(client, "/#/documents");
        var cookie = SessionCookie(await Callback(client, handshake, authority.Code(handshake.Query)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/csrf");
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new(cookie, string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])),
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("requestToken").GetString()!);
    }

    private static Task<HttpResponseMessage> Logout(HttpClient client, SessionCredentials session, bool csrf = true, CancellationToken cancellation = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/oidc/logout");
        request.Headers.Add("Cookie", session.Cookie + "; " + session.CsrfCookie);
        if (csrf) request.Headers.Add(AdminOidcConstants.CsrfHeader, session.CsrfToken);
        return client.SendAsync(request, cancellation);
    }

    private static async Task<JsonElement> LogoutData(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
    }

    private static async Task AssertRevoked(HttpClient client, string cookie)
    {
        foreach (var route in new[] { ProtectedApiRoute, "/admin/auth/oidc/session" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Add("Cookie", cookie);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("Re-authentication is required.", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task PreparedLogoutRevokesFirstUsesServerSnapshotAndReturnsOpaqueUrlOnly()
    {
        using var authority = new OidcTestAuthority { HoldLogout = true };
        var logs = new OidcLogCapture();
        using var factory = OidcFactory(authority, configure: services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.Configure<LoggerFilterOptions>(options => options.MinLevel = LogLevel.Trace);
            services.AddSingleton<ILoggerFactory>(provider => new LoggerFactory([logs], provider.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>()));
        });
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/session");
        sessionRequest.Headers.Add("Cookie", session.Cookie);
        var active = await client.SendAsync(sessionRequest);
        var status = (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("authenticated", status.GetProperty("reason").GetString());
        Assert.Equal("fake-name", status.GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await Logout(client, session, csrf: false)).StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<MemoryTicketStore>().Count);

        var pending = Logout(client, session);
        await authority.LogoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertRevoked(client, session.Cookie);
        var form = Assert.Single(authority.LogoutForms);
        Assert.Equal(new[] { "client_id", "client_secret", "id_token_hint", "post_logout_redirect_uri", "state" }, form.Keys.Order().ToArray());
        Assert.Equal(authority.LastIdToken, form["id_token_hint"]);
        Assert.Equal(OidcTestAuthority.Secret, form["client_secret"]);
        Assert.Equal(OidcTestAuthority.PostLogoutUri, form["post_logout_redirect_uri"]);
        Assert.Equal(OidcTestAuthority.ClientId, form["client_id"]);
        authority.LogoutRelease.TrySetResult();
        using var response = await pending;
        var data = await LogoutData(response);
        Assert.Equal("logoutPrepared", data.GetProperty("reason").GetString());
        Assert.Equal(OidcTestAuthority.Issuer + "/oauth2/logout?logout_handle=" + new string('h', 43), data.GetProperty("logoutUrl").GetString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(AdminOidcConstants.SessionCookie + "=;") || cookie.StartsWith(AdminOidcConstants.SessionCookie + "="));
        var binding = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AdminOidcConstants.LogoutBindingCookie + "="));
        Assert.Contains("httponly", binding.ToLowerInvariant());
        Assert.Contains("secure", binding.ToLowerInvariant());
        Assert.Contains("samesite=lax", binding.ToLowerInvariant());
        using var returnedRequest = new HttpRequestMessage(HttpMethod.Get, AdminOidcConstants.LogoutReturnPath + "?state=" + form["state"]);
        returnedRequest.Headers.Add("Cookie", binding.Split(';')[0]);
        using var returned = await client.SendAsync(returnedRequest);
        Assert.Equal(AdminOidcLogout.Returned, returned.Headers.Location!.OriginalString);
        var surfaces = await response.Content.ReadAsStringAsync() + string.Join('\n', response.Headers) + returned.Headers.Location + string.Join('\n', logs.Messages);
        foreach (var canary in new[] { OidcTestAuthority.Secret, authority.LastAccessToken!, authority.LastIdToken!, authority.LastVerifier!, form["state"] })
            Assert.DoesNotContain(canary, surfaces);
        Assert.DoesNotContain("fictitious-code-", surfaces);
        await AssertRevoked(client, session.Cookie);
    }

    [Fact]
    public async Task ParallelAndRepeatedLogoutMakesAtMostOneProviderCall()
    {
        using var authority = new OidcTestAuthority { HoldLogout = true };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var pending = Enumerable.Range(0, 6).Select(_ => Logout(client, session)).ToArray();
        await authority.LogoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertRevoked(client, session.Cookie);
        authority.LogoutRelease.TrySetResult();
        var responses = await Task.WhenAll(pending);
        var results = new List<string>();
        foreach (var response in responses) results.Add((await LogoutData(response)).GetProperty("reason").GetString()!);
        Assert.Single(results, reason => reason == "logoutPrepared");
        Assert.Equal(5, results.Count(reason => reason == "localSignedOut"));
        Assert.Equal("localSignedOut", (await LogoutData(await Logout(client, session))).GetProperty("reason").GetString());
        Assert.Single(authority.LogoutForms);
        Assert.Equal(0, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
        await AssertRevoked(client, session.Cookie);
    }

    [Theory]
    [InlineData("500")][InlineData("404")][InlineData("json")][InlineData("timeout")][InlineData("oversized")][InlineData("duplicate")]
    public async Task ProviderFailureLeavesLocalRevocationAndNeverRetries(string failure)
    {
        using var authority = new OidcTestAuthority { LogoutFailure = failure };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var data = await LogoutData(await Logout(client, session));
        Assert.Equal("localSignedOut", data.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("logoutUrl").ValueKind);
        await AssertRevoked(client, session.Cookie);
        await Logout(client, session);
        Assert.Single(authority.LogoutForms);
        Assert.Equal(0, factory.Services.GetRequiredService<LogoutReturnStore>().Count);
    }

    [Fact]
    public async Task ActualBackchannelTimeoutLeavesSessionRevoked()
    {
        using var authority = new OidcTestAuthority { HoldLogout = true };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var pending = Logout(client, session);
        await authority.LogoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertRevoked(client, session.Cookie);
        var data = await LogoutData(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("localSignedOut", data.GetProperty("reason").GetString());
        await Logout(client, session);
        Assert.Single(authority.LogoutForms);
        Assert.Equal(0, factory.Services.GetRequiredService<LogoutReturnStore>().Count);
    }

    [Theory]
    [InlineData("nonce", "admin")][InlineData("valid", "user")][InlineData("valid", "sub-mismatch")]
    public async Task RejectedCallbackDoesNotReplaceOrExtendExistingSessionAndDoesNotLeak(string idDefect, string accessDefect)
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
        var session = await SignInWithCsrf(client, authority);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query, idDefect, accessDefect);
        var response = await Callback(client, handshake, code, cookies: handshake.Cookies + "; " + session.Cookie);
        Failed(response, accessDefect == "user" ? AdminOidcRegistration.DeniedRedirect : AdminOidcRegistration.SignInFailedRedirect);
        using var original = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/session");
        original.Headers.Add("Cookie", session.Cookie);
        var status = await client.SendAsync(original);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(1, factory.Services.GetRequiredService<MemoryTicketStore>().Count);
        var surfaces = await response.Content.ReadAsStringAsync() + string.Join('\n', response.Headers) + response.Headers.Location
            + await status.Content.ReadAsStringAsync() + string.Join('\n', logs.Messages);
        foreach (var canary in new[] { OidcTestAuthority.Secret, authority.LastAccessToken!, authority.LastIdToken!, authority.LastVerifier!, code, handshake.Query["state"] })
            Assert.DoesNotContain(canary, surfaces);
    }

    [Theory]
    [InlineData("?returnUrl=/&returnUrl=/second")][InlineData("?returnUrl=/&unexpected=x")]
    public async Task DuplicateOrUnknownStartInputsCreateNoPending(string query)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var response = await client.GetAsync("/admin/auth/oidc/start" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<CompactStateDataFormat>().Count);
    }

    [Fact]
    public async Task LogoutCancellationDoesNotResurrectLocalSession()
    {
        using var authority = new OidcTestAuthority { HoldLogout = true };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        using var cancellation = new CancellationTokenSource();
        var pending = Logout(client, session, cancellation: cancellation.Token);
        await authority.LogoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await AssertRevoked(client, session.Cookie);
        await Logout(client, session);
        Assert.Single(authority.LogoutForms);
        Assert.Equal(0, factory.Services.GetRequiredService<LogoutReturnStore>().Count);
    }

    [Theory]
    [InlineData("https://external.example/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh")]
    [InlineData("//external.example/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh")]
    [InlineData("https://user@identity.example.test/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh")]
    [InlineData("/oauth2/logout?logout_handle=short")]
    [InlineData("/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh&state=canary")]
    [InlineData("/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh&logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh")]
    [InlineData("/oauth2/logout?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh#fragment")]
    [InlineData("/wrong?logout_handle=hhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhhh")]
    public async Task UnsafeLogoutNavigationIsNotReleased(string url)
    {
        using var authority = new OidcTestAuthority { LogoutUrl = url };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var data = await LogoutData(await Logout(client, session));
        Assert.Equal("localSignedOut", data.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("logoutUrl").ValueKind);
        await AssertRevoked(client, session.Cookie);
    }

    [Theory]
    [InlineData("valid")][InlineData("missing-cookie")][InlineData("wrong-cookie")][InlineData("wrong-state")]
    [InlineData("expired")][InlineData("duplicate")][InlineData("unknown-parameter")]
    public async Task LogoutReturnRequiresUnexpiredBrowserBindingAndIsSingleUse(string scenario)
    {
        using var authority = new OidcTestAuthority();
        var time = new ManualOidcTime();
        using var factory = OidcFactory(authority, time);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        using var loggedOut = await Logout(client, session);
        var binding = loggedOut.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(AdminOidcConstants.LogoutBindingCookie + "=")).Split(';')[0];
        var state = Assert.Single(authority.LogoutForms)["state"];
        if (scenario == "expired") time.Advance(LogoutReturnStore.Lifetime);
        var query = "?state=" + (scenario == "wrong-state" ? new string('x', 43) : state);
        if (scenario == "duplicate") query += "&state=" + state;
        if (scenario == "unknown-parameter") query += "&returnUrl=https://external.example";
        async Task<HttpResponseMessage> Return()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, AdminOidcConstants.LogoutReturnPath + query);
            if (scenario != "missing-cookie") request.Headers.Add("Cookie", scenario == "wrong-cookie" ? AdminOidcConstants.LogoutBindingCookie + "=" + new string('x', 43) : binding);
            return await client.SendAsync(request);
        }
        var returned = await Return();
        Assert.Equal(scenario == "valid" ? AdminOidcLogout.Returned : AdminOidcLogout.InvalidReturn, returned.Headers.Location!.OriginalString);
        Assert.Equal(AdminOidcLogout.InvalidReturn, (await Return()).Headers.Location!.OriginalString);
        await AssertRevoked(client, session.Cookie);
        Assert.DoesNotContain(state, returned.Headers.Location.OriginalString);
    }

    [Theory]
    [InlineData(false, true)][InlineData(true, true)][InlineData(false, false)][InlineData(true, false)]
    public async Task MixedBearerAndSessionCookieWritesRequireActuallyVerifiedBearer(bool expired, bool validBearer)
    {
        using var authority = new OidcTestAuthority();
        var time = new ManualOidcTime();
        using var factory = OidcFactory(authority, time);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        if (expired) time.Advance(TimeSpan.FromMinutes(16));
        using var request = new HttpRequestMessage(HttpMethod.Delete, ProtectedApiRoute + "/00000000-0000-0000-0000-000000000047");
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("Authorization", "Bearer " + (validBearer ? CreateToken("admin") : "fictitious-invalid-token"));
        var response = await client.SendAsync(request);
        Assert.Equal(validBearer ? HttpStatusCode.NotFound : expired ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        // Logout always acts on a session and cannot use a Bearer header to bypass its CSRF.
        if (!expired)
        {
            using var logout = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/oidc/logout");
            logout.Headers.Add("Cookie", session.Cookie);
            logout.Headers.Add("Authorization", "Bearer " + CreateToken("admin"));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(logout)).StatusCode);
            Assert.Empty(authority.LogoutForms);
        }
    }

    [Fact]
    public async Task BearerPrincipalCannotBorrowCookieAdministratorRole()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("Authorization", "Bearer " + CreateToken("user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
}
