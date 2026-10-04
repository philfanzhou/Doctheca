using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Doctheca.Host.Authentication;

/// <summary>Local revocation precedes every prepared logout attempt, including cancellation.</summary>
internal static class AdminOidcLogout
{
    internal const string Returned = "/?authResult=signedOut";
    internal const string InvalidReturn = "/?authError=logoutReturnFailed";

    internal static async Task<IResult> Logout(HttpContext context, IAntiforgery antiforgery,
        IOptionsMonitor<CookieAuthenticationOptions> cookies, IOptionsMonitor<OpenIdConnectOptions> oidc,
        MemoryTicketStore tickets, LogoutReturnStore returns, AdminOidcSettings settings)
    {
        if (!settings.Available) return AdminOidcEndpoints.Disabled();
        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        if (session.Succeeded)
        {
            context.User = session.Principal!;
            try
            {
                if (!context.Request.Headers.TryGetValue(AdminOidcConstants.CsrfHeader, out var header)
                    || header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
                    throw new AntiforgeryValidationException("oidc.csrf_required");
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.Json(new { success = false, message = AdminOidcSessionMiddleware.CsrfRequired }, statusCode: 403);
            }
        }

        // Cookie middleware stores an encrypted reference ticket under this framework claim.
        // Re-read its reference, then atomically take the persisted snapshot, never the earlier
        // authentication snapshot (which another request may already have revoked).
        var options = cookies.Get(AdminOidcConstants.SessionScheme);
        var value = options.CookieManager.GetRequestCookie(context, options.Cookie.Name!);
        var reference = value is null ? null : options.TicketDataFormat.Unprotect(value)?.Principal
            .FindFirst("Microsoft.AspNetCore.Authentication.Cookies-SessionId")?.Value;
        var snapshot = session.Succeeded && reference is not null ? tickets.Revoke(reference) : null;
        await context.SignOutAsync(AdminOidcConstants.SessionScheme);
        context.Response.Cookies.Delete(AdminOidcConstants.CsrfCookie, new CookieOptions { Path = "/", Secure = !settings.InsecureLoopback, HttpOnly = true, SameSite = SameSiteMode.Lax });
        var idToken = snapshot?.Properties.GetTokenValue("id_token");
        if (string.IsNullOrEmpty(idToken)) return LocalOnly();

        string? state = null;
        var bindingOptions = BindingOptions(settings);
        try
        {
            var pending = returns.Create();
            state = pending.State;
            // No request is retried, including an ambiguous provider response.
            var backchannel = oidc.Get(AdminOidcConstants.OidcScheme).Backchannel;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Authority + "/oauth2/logout/requests")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = settings.ClientId, ["client_secret"] = settings.ClientSecret,
                    ["id_token_hint"] = idToken, ["post_logout_redirect_uri"] = settings.PostLogoutRedirectUri,
                    ["state"] = state
                })
            };
            using var response = await backchannel.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            response.EnsureSuccessStatusCode();
            // Bound the untrusted response before parsing or exposing any navigation target.
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            var buffer = new byte[16385];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), budget.Token);
                if (read == 0) break;
                length += read;
            }
            if (length > 16384) throw new InvalidOperationException("oidc.invalid_logout_response");
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            var properties = json.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "logout_uri" || properties[0].Value.ValueKind != JsonValueKind.String
                || !TryLogoutUrl(properties[0].Value.GetString(), settings, out var logoutUrl))
                throw new InvalidOperationException("oidc.invalid_logout_response");
            context.Response.Cookies.Append(AdminOidcConstants.LogoutBindingCookie, pending.Binding, bindingOptions);
            return Results.Ok(new { success = true, message = "Local session signed out.", data = new { reason = "logoutPrepared", logoutUrl } });
        }
        catch
        {
            // Even request cancellation cannot undo local revocation or release a navigation
            // URL whose response was incomplete. The fixed result claims only local logout.
            if (state is not null) returns.Remove(state);
            context.Response.Cookies.Delete(AdminOidcConstants.LogoutBindingCookie, bindingOptions);
            return LocalOnly();
        }
    }

    internal static IResult Return(HttpContext context, AdminOidcSettings settings, LogoutReturnStore returns)
    {
        if (!settings.Available) return AdminOidcEndpoints.Disabled();
        var query = context.Request.Query;
        var valid = query.Count == 1 && query.TryGetValue("state", out var state) && state.Count == 1
            && returns.Consume(state[0], context.Request.Cookies[AdminOidcConstants.LogoutBindingCookie]);
        context.Response.Cookies.Delete(AdminOidcConstants.LogoutBindingCookie, BindingOptions(settings));
        return Results.Redirect(valid ? Returned : InvalidReturn);
    }

    internal static bool TryLogoutUrl(string? value, AdminOidcSettings settings, out string? url)
    {
        url = null;
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\')) return false;
        var authority = new Uri(settings.Authority);
        if (!(value.StartsWith('/') && !value.StartsWith("//") || Uri.IsWellFormedUriString(value, UriKind.Absolute))
            || !Uri.TryCreate(authority, value, out var parsed) || parsed.GetLeftPart(UriPartial.Authority) != authority.GetLeftPart(UriPartial.Authority)
            || parsed.AbsolutePath != "/oauth2/logout" || parsed.UserInfo != "" || parsed.Fragment != "") return false;
        var query = QueryHelpers.ParseQuery(parsed.Query);
        if (query.Count != 1 || !query.TryGetValue("logout_handle", out var handle) || handle.Count != 1
            || handle[0] is not { Length: 43 } raw || raw.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return false;
        // Canonical construction prevents alternate encodings/duplicate query fields escaping
        // the public SignaCore prepared-logout contract.
        var canonical = settings.Authority + "/oauth2/logout?logout_handle=" + raw;
        if (value != canonical && value != "/oauth2/logout?logout_handle=" + raw) return false;
        url = canonical;
        return true;
    }

    private static CookieOptions BindingOptions(AdminOidcSettings settings) => new()
    { Path = AdminOidcConstants.LogoutReturnPath, HttpOnly = true, Secure = !settings.InsecureLoopback, SameSite = SameSiteMode.Lax, MaxAge = LogoutReturnStore.Lifetime };

    private static IResult LocalOnly() => Results.Ok(new
    { success = true, message = "Local session signed out.", data = new { reason = "localSignedOut", logoutUrl = (string?)null } });
}
