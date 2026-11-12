using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Doctheca.Host.Authentication;
using Doctheca.Tests.Authentication;
using Microsoft.AspNetCore.WebUtilities;
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
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
    }

    private static Task<HttpResponseMessage> Logout(HttpClient client, SessionCredentials session, bool csrf = true, CancellationToken cancellation = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/oidc/logout");
        request.Headers.Add("Cookie", session.Cookie + "; " + session.CsrfCookie);
        if (csrf) request.Headers.Add(AdminOidcConstants.CsrfHeader, session.CsrfToken);
        return client.SendAsync(request, cancellation);
    }

    /// <summary>A CSRF credential pair obtained without any session (the issuer is public).</summary>
    private static async Task<SessionCredentials> AnonymousCsrf(HttpClient client)
    {
        using var response = await client.GetAsync("/admin/auth/oidc/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new("", string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0])),
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
    }

    private static async Task<JsonElement> LocalOnly(HttpResponseMessage response)
    {
        // The fixed local-only outcome: the local session was revoked (or there was none) and no
        // upstream redirect could be handed to the browser.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("local_only", body.GetProperty("outcome").GetString());
        return body;
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

    private static void AssertSessionCookieDeleted(HttpResponseMessage response) => Assert.Contains(
        response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(AdminOidcConstants.SessionCookie + "="));

    [Fact]
    public async Task StandardLogoutCarriesIdTokenHintAndRegisteredPostLogoutUri()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        using var sessionRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/session");
        sessionRequest.Headers.Add("Cookie", session.Cookie);
        var active = await client.SendAsync(sessionRequest);
        var status = (await active.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(status.GetProperty("authenticated").GetBoolean());
        Assert.Equal("authenticated", status.GetProperty("reason").GetString());
        Assert.Equal("fake-name", status.GetProperty("username").GetString());
        Assert.NotNull(status.GetProperty("expiresAt").GetString());
        // A logout without its CSRF credential is rejected by the boundary with its fixed 400
        // body and touches nothing.
        var rejected = await Logout(client, session, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("csrf_rejected", (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString());
        Assert.Equal(1, Tickets(factory).Count);

        using var response = await Logout(client, session);
        // The standard RP-initiated logout: one redirect to the authority's end-session endpoint,
        // carrying the server-held ID token as id_token_hint, the registered post-logout URI, and
        // the protected state that lands the browser back on the SPA result.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(OidcTestAuthority.Issuer + OidcTestAuthority.EndSessionPath, location.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(authority.LastIdToken, query["id_token_hint"].ToString());
        Assert.Equal(OidcTestAuthority.PostLogoutUri, query["post_logout_redirect_uri"].ToString());
        var state = query["state"].ToString();
        Assert.False(string.IsNullOrEmpty(state));
        AssertSessionCookieDeleted(response);
        // The standard flow needs no extra correlation cookie: the protected state round-trips
        // through the authority's own redirect.
        Assert.DoesNotContain(response.Headers.GetValues("Set-Cookie"), cookie => cookie.Contains("logout-return"));
        await AssertRevoked(client, session.Cookie);
        // No second server-to-server call exists in the standard flow.
        Assert.Equal(1, authority.Redeems);

        using var returnedRequest = new HttpRequestMessage(HttpMethod.Get, AdminOidcConstants.LogoutReturnPath + "?state=" + Uri.EscapeDataString(state));
        using var returned = await client.SendAsync(returnedRequest);
        Assert.Equal(HttpStatusCode.Redirect, returned.StatusCode);
        Assert.Equal(AdminOidcConstants.SignedOutRedirect, returned.Headers.Location!.OriginalString);
        // The return is idempotent (its state is a protected, server-independent value): a replay
        // lands on the same SPA result instead of the package's one-time 400.
        using var replay = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, returnedRequest.RequestUri));
        Assert.Equal(HttpStatusCode.Redirect, replay.StatusCode);
        Assert.Equal(AdminOidcConstants.SignedOutRedirect, replay.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("missing-state")][InlineData("wrong-state")][InlineData("duplicate-state")]
    public async Task LogoutReturnWithoutProtectedStateAnswersFixed400Page(string defect)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var state = defect == "wrong-state" ? "fictitious-state-canary" : "plain-random-padding";
        var query = "?state=" + (defect == "missing-state" ? "" : Uri.EscapeDataString(state));
        if (defect == "duplicate-state") query += "&state=" + Uri.EscapeDataString(state);
        using var response = await client.GetAsync(AdminOidcConstants.LogoutReturnPath + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Sign-out could not be confirmed", await response.Content.ReadAsStringAsync());
        // The rejected return never touches the live session.
        using var sessionProbe = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/oidc/session");
        sessionProbe.Headers.Add("Cookie", session.Cookie);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(sessionProbe)).StatusCode);
        Assert.Equal(1, Tickets(factory).Count);
    }

    [Fact]
    public async Task MissingEndSessionEndpointLeavesLocalRevocationOnly()
    {
        using var authority = new OidcTestAuthority { EndSessionUnavailable = true };
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        var response = await Logout(client, session);
        AssertSessionCookieDeleted(response);
        await LocalOnly(response);
        await AssertRevoked(client, session.Cookie);
        await LocalOnly(await Logout(client, session));
        Assert.Equal(1, authority.Redeems);
    }

    [Fact]
    public async Task LogoutWithoutLiveSessionAnswersLocalOnlyAndTouchesNothing()
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority);
        using var client = Browser(factory);
        var credentials = await AnonymousCsrf(client);
        using var response = await Logout(client, credentials);
        await LocalOnly(response);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(0, Tickets(factory).Count);
    }

    [Fact]
    public async Task ExpiredSessionLogoutAnswersLocalOnly()
    {
        using var authority = new OidcTestAuthority();
        var time = new ManualOidcTime();
        using var factory = OidcFactory(authority, time);
        using var client = Browser(factory);
        var session = await SignInWithCsrf(client, authority);
        time.Advance(TimeSpan.FromMinutes(16));
        using var response = await Logout(client, session);
        await LocalOnly(response);
        await AssertRevoked(client, session.Cookie);
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
    }

    [Theory]
    [InlineData(false, true)][InlineData(true, true)][InlineData(false, false)][InlineData(true, false)]
    [InlineData(false, false, true)][InlineData(false, true, true)]
    public async Task MixedBearerAndSessionCookieWritesRequireActuallyVerifiedBearer(bool expired, bool validBearer, bool http = false)
    {
        using var authority = new OidcTestAuthority();
        var time = new ManualOidcTime();
        using var factory = OidcFactory(authority, time, http: http);
        using var client = Browser(factory, http);
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
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(logout)).StatusCode);
            Assert.Equal(1, authority.Redeems);
        }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task BearerPrincipalCannotBorrowCookieAdministratorRole(bool http)
    {
        using var authority = new OidcTestAuthority();
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
        var session = await SignInWithCsrf(client, authority);
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedApiRoute);
        request.Headers.Add("Cookie", session.Cookie);
        request.Headers.Add("Authorization", "Bearer " + CreateToken("user"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }
}
