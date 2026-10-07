using Microsoft.AspNetCore.Http;
using SignaCore.Client.AspNetCore;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Presents the package's protocol outcomes in this host's fixed JSON/redirect shapes (issue
/// #70). The package defines the outcomes; this writer keeps the browser-facing contract of the
/// replaced self-written slice: failures redirect to the SPA root with one bounded
/// <c>authError</c> value, an invalid return URL answers the fixed 400 body, and the session
/// endpoint keeps its <c>success</c>/<c>data</c> envelope.
/// </summary>
/// <remarks>
/// Reason mapping: <see cref="SignaCoreSignInReason.AccessDenied"/> covers both the user's
/// cancellation and a gate denial (the package does not separate them), so both surface as
/// <c>authError=notAdmin</c>; discovery and token-endpoint transport failures surface as
/// <c>authError=identityUnavailable</c>; every other protocol failure surfaces as
/// <c>authError=signInFailed</c>. No other failure detail ever reaches the browser.
/// </remarks>
public sealed class AdminLoginResponseWriter : ISignaCoreHostedLoginResponseWriter
{
    public static readonly AdminLoginResponseWriter Instance = new();

    internal const string SignInFailedRedirect = "/?authError=signInFailed";
    internal const string DeniedRedirect = "/?authError=notAdmin";
    internal const string IdentityUnavailableRedirect = "/?authError=identityUnavailable";
    internal const string InvalidReturnUrlMessage = "The return URL must be a path inside this site.";
    internal const string ReauthenticationRequired = "Re-authentication is required.";

    public Task WriteSignInFailureAsync(HttpContext context, SignaCoreSignInReason reason,
        CancellationToken cancellationToken)
    {
        if (reason == SignaCoreSignInReason.InvalidReturnUrl)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return context.Response.WriteAsJsonAsync(
                new { success = false, message = InvalidReturnUrlMessage }, cancellationToken);
        }
        context.Response.Redirect(RedirectTarget(reason));
        return Task.CompletedTask;
    }

    public Task WriteFailurePageAsync(HttpContext context, SignaCoreSignInReason? reason,
        CancellationToken cancellationToken)
    {
        // The failure page segment is only reached by a browser navigation that the failure
        // redirect above never produces; answer it with the same bounded redirect so the SPA
        // renders a fixed outcome instead of a package page.
        context.Response.Redirect(reason is { } value ? RedirectTarget(value) : SignInFailedRedirect);
        return Task.CompletedTask;
    }

    public async Task WriteSessionStatusAsync(HttpContext context, SignaCoreSessionStatus status,
        CancellationToken cancellationToken)
    {
        if (!status.Authenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = ReauthenticationRequired,
                data = new { authenticated = false, reason = "reauthenticationRequired" }
            }, cancellationToken);
            return;
        }

        // The expiry is read back from the server-side ticket behind the presented opaque
        // cookie, the same value the replaced slice reported from its session properties.
        DateTimeOffset? expiresAt = null;
        if (context.Request.Cookies.TryGetValue(AdminOidcConstants.SessionCookie, out var key)
            && !string.IsNullOrEmpty(key))
        {
            var ticket = await context.RequestServices.GetRequiredService<ITicketStore>()
                .RetrieveAsync(key, cancellationToken);
            expiresAt = ticket?.ExpiresUtc;
        }
        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            success = true,
            data = new
            {
                authenticated = true,
                reason = "authenticated",
                username = status.DisplayName,
                expiresAt
            }
        }, cancellationToken);
    }

    internal static string RedirectTarget(SignaCoreSignInReason reason) => reason switch
    {
        SignaCoreSignInReason.AccessDenied => DeniedRedirect,
        SignaCoreSignInReason.AuthorityUnreachable => IdentityUnavailableRedirect,
        _ => SignInFailedRedirect,
    };
}
