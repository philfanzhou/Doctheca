using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Doctheca.Host.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Doctheca.Tests.Integration;

public sealed partial class AdminOidcIntegrationTests
{
    private static string Carrier(HttpResponseMessage response, string name) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith(name + "=", StringComparison.Ordinal));

    private static void CookieProfile(string cookie, bool http, string path, string sameSite, bool cleared = false)
    {
        var parts = cookie.Split(';').Skip(1).Select(value => value.Trim().ToLowerInvariant()).ToArray();
        Assert.Equal(!http, parts.Contains("secure"));
        Assert.Contains("httponly", parts);
        Assert.Contains("path=" + path, parts);
        Assert.Contains("samesite=" + sameSite, parts);
        Assert.DoesNotContain(parts, value => value.StartsWith("domain="));
        if (cleared) Assert.Contains(parts, value => value == "expires=thu, 01 jan 1970 00:00:00 gmt");
    }

    [Theory]
    [InlineData(false, false)][InlineData(false, true)]
    [InlineData(true, false)][InlineData(true, true)]
    public async Task AllCarrierCookiesFollowConfiguredCallbackIncludingClearingAndRequestMismatch(bool http, bool inboundHttp)
    {
        using var authority = new OidcTestAuthority();
        var logs = new OidcLogCapture();
        using var factory = OidcFactory(authority, configure: services =>
        {
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs);
        }, http: http);
        using var client = Browser(factory, inboundHttp);
        var csrfOptions = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;
        Assert.Equal(http ? CookieSecurePolicy.None : CookieSecurePolicy.Always, csrfOptions.Cookie.SecurePolicy);
        Assert.Equal(AdminOidcConstants.CsrfCookie, csrfOptions.Cookie.Name);
        Assert.Equal(AdminOidcConstants.CsrfHeader, csrfOptions.HeaderName);
        // Effective options retain the configured profile even behind a TLS-terminating proxy.
        var context = new DefaultHttpContext();
        context.Request.Scheme = inboundHttp ? "http" : "https";
        Assert.Equal(!http, csrfOptions.Cookie.Build(context).Secure);

        var handshake = await Start(client, "/#search");
        var bindingName = handshake.BindingCookie.Split('=')[0];
        CookieProfile(Carrier(handshake.Response, bindingName), http, AdminOidcConstants.CallbackPath, "lax");
        Assert.Contains("expires=", Carrier(handshake.Response, bindingName));
        var code = authority.Code(handshake.Query);
        using var signedIn = await Callback(client, handshake, code);
        Assert.Equal("/#search", signedIn.Headers.Location!.OriginalString);
        CookieProfile(Carrier(signedIn, bindingName), http, AdminOidcConstants.CallbackPath, "lax", cleared: true);
        CookieProfile(Carrier(signedIn, AdminOidcConstants.SessionCookie), http, "/", "lax");
        Assert.Contains("expires=", Carrier(signedIn, AdminOidcConstants.SessionCookie));
        var sessionCookie = SessionCookie(signedIn);
        // ASP.NET requires an effective HTTPS request to issue and validate Always-secure
        // antiforgery credentials. A TLS proxy must keep its existing trusted forwarded-scheme configuration.
        using var csrfRequest = new HttpRequestMessage(HttpMethod.Get,
            "https://admin.example.test/admin/auth/oidc/csrf");
        csrfRequest.Headers.Add("Cookie", sessionCookie);
        using var csrf = await client.SendAsync(csrfRequest);
        Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
        var csrfCarrier = Carrier(csrf, AdminOidcConstants.CsrfCookie);
        CookieProfile(csrfCarrier, http, "/", "strict");
        var token = (await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var credentials = new SessionCredentials(sessionCookie, csrfCarrier.Split(';')[0], token);
        using var csrfClient = Browser(factory, http);
        using var loggedOut = await Logout(csrfClient, credentials);
        Assert.Equal(HttpStatusCode.Redirect, loggedOut.StatusCode);
        CookieProfile(Carrier(loggedOut, AdminOidcConstants.SessionCookie), http, "/", "lax", cleared: true);
        var returnCarrier = Carrier(loggedOut, LogoutReturnCookie);
        CookieProfile(returnCarrier, http, "/admin/auth/oidc/logout", "lax");
        Assert.Contains("expires=", returnCarrier);
        using var returnRequest = new HttpRequestMessage(HttpMethod.Get,
            AdminOidcConstants.LogoutReturnPath + "?state=" + Assert.Single(authority.LogoutForms)["state"]);
        returnRequest.Headers.Add("Cookie", returnCarrier.Split(';')[0]);
        using var returned = await client.SendAsync(returnRequest);
        Assert.Equal(AdminOidcConstants.SignedOutRedirect, returned.Headers.Location!.OriginalString);
        CookieProfile(Carrier(returned, LogoutReturnCookie), http, "/admin/auth/oidc/logout", "lax", cleared: true);
        using var replay = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, returnRequest.RequestUri)
        { Headers = { { "Cookie", returnCarrier.Split(';')[0] } } });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        await AssertRevoked(client, sessionCookie);
        var surfaces = string.Join('\n', logs.Messages) + await signedIn.Content.ReadAsStringAsync()
            + await loggedOut.Content.ReadAsStringAsync() + returned.Headers.Location;
        foreach (var canary in new[] { OidcTestAuthority.Secret, code, authority.LastAccessToken!, authority.LastIdToken!, authority.LastVerifier! })
            Assert.DoesNotContain(canary, surfaces);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task CancelledCallbackConsumesStateWithoutSession(bool http)
    {
        using var authority = new OidcTestAuthority { HoldToken = true };
        using var factory = OidcFactory(authority, http: http);
        using var client = Browser(factory, http);
        var handshake = await Start(client);
        var code = authority.Code(handshake.Query);
        using var cancellation = new CancellationTokenSource();
        var callback = Callback(client, handshake, code, cancellation: cancellation.Token);
        await authority.TokenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callback);
        Assert.Equal(0, Tickets(factory).Count);
        Failed(await Callback(client, handshake, code));
        Assert.Equal(1, authority.Redeems);
    }
}
