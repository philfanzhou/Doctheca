using System.Net.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The fixed presentation layer of the admin hosted-login slice (the framework's OpenID Connect
/// handler defines the protocol; this writer keeps the browser-facing contract of the replaced
/// package integration): failures redirect to the SPA root with one bounded <c>authError</c>
/// value, an invalid return URL answers the fixed 400 body, and the session endpoint keeps its
/// <c>success</c>/<c>data</c> envelope.
/// </summary>
/// <remarks>
/// Reason mapping: the pre-sign-in admin-gate denial and the user's cancellation both surface as
/// <c>authError=notAdmin</c>; transport failures of the authority's discovery, JWKS, or token
/// endpoints — backchannel timeouts included, which the timeout handler normalizes to
/// <see cref="HttpRequestException"/> — surface as <c>authError=identityUnavailable</c>; every
/// other protocol failure surfaces as <c>authError=signInFailed</c>. No other failure detail ever
/// reaches the browser.
/// </remarks>
public static class AdminLoginResponseWriter
{
    internal const string SignInFailedRedirect = "/?authError=signInFailed";
    internal const string DeniedRedirect = "/?authError=notAdmin";
    internal const string IdentityUnavailableRedirect = "/?authError=identityUnavailable";
    internal const string InvalidReturnUrlMessage = "The return URL must be a path inside this site.";
    internal const string ReauthenticationRequired = "Re-authentication is required.";

    /// <summary>
    /// The fixed landing of one sign-in failure. The decision input is the closed failure set of
    /// the OpenID Connect handler; the mapping keeps the package integration's bounded outcomes.
    /// </summary>
    internal static string RedirectTarget(Exception? failure)
    {
        if (failure is null)
        {
            return SignInFailedRedirect;
        }

        if (failure.Message == AdminOidcConstants.NotAdminFailure)
        {
            return DeniedRedirect;
        }

        if (failure is OpenIdConnectProtocolException protocol
            && string.Equals(
                protocol.Data["error"] as string,
                "access_denied",
                StringComparison.Ordinal))
        {
            // The user's cancellation merges with the denial outcome, exactly as before.
            return DeniedRedirect;
        }

        return IsAuthorityUnavailable(failure) ? IdentityUnavailableRedirect : SignInFailedRedirect;
    }

    /// <summary>
    /// Whether the failure chain is a transport failure of the authority's endpoints — the
    /// identity-unavailable outcome. IdentityModel wraps discovery and document retrieval errors
    /// (for example <c>IOException</c>/<c>InvalidOperationException</c> around IDX20803/IDX20807),
    /// so every link of the chain is inspected.
    /// </summary>
    internal static bool IsAuthorityUnavailable(Exception failure)
    {
        for (var exception = failure; exception is not null; exception = exception.InnerException)
        {
            if (exception is HttpRequestException or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The fixed 400 body of an invalid sign-in-entry return URL; no pending sign-in is
    /// ever created for the rejected input.</summary>
    internal static Task WriteInvalidReturnUrlAsync(HttpContext context, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(
            new { success = false, message = InvalidReturnUrlMessage }, cancellationToken);
    }

    /// <summary>
    /// The session-status endpoint's two fixed shapes. An anonymous, unknown, or expired session
    /// answers the fixed re-authentication result; a live session reports its display name and the
    /// expiry read back from the server-side ticket, never a token.
    /// </summary>
    internal static async Task WriteSessionStatusAsync(
        HttpContext context, AuthenticateResult session, CancellationToken cancellationToken)
    {
        if (!session.Succeeded || session.Principal is null)
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

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(new
        {
            success = true,
            data = new
            {
                authenticated = true,
                reason = "authenticated",
                username = session.Principal.FindFirst("name")?.Value,
                expiresAt = session.Properties?.ExpiresUtc
            }
        }, cancellationToken);
    }
}
