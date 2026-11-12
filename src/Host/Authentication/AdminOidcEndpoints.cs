using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The hosted-login endpoints. Missing configuration returns fixed 503 responses; with a complete
/// configuration this host maps the sign-in start, the antiforgery-token issuer, the session
/// status, and the logout, while the framework's OpenID Connect handler itself intercepts the
/// callback and the sign-out callback on their configured paths. Every route is anonymous: the
/// session boundary never applies under <c>/admin/auth</c>, and each endpoint enforces its own
/// fixed outcomes.
/// </summary>
public static class AdminOidcEndpoints
{
    public static WebApplication MapAdminOidcEndpoints(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AdminOidcSettings>();
        if (!settings.Available)
        {
            var disabled = app.MapGroup("/admin/auth")
                .RequireServiceMantleSecurityResponseHeaders();
            disabled.MapGet("/oidc/start", Disabled).AllowAnonymous();
            disabled.MapGet("/oidc/callback", Disabled).AllowAnonymous();
            disabled.MapGet("/oidc/csrf", Disabled).AllowAnonymous();
            disabled.MapGet("/oidc/session", Disabled).AllowAnonymous();
            disabled.MapPost("/oidc/logout", Disabled).AllowAnonymous();
            disabled.MapGet("/oidc/logout/return", Disabled).AllowAnonymous();
            return app;
        }

        var group = app.MapGroup("/admin/auth")
            .RequireServiceMantleSecurityResponseHeaders()
            .AllowAnonymous();
        group.MapGet("/oidc/start", Start);
        group.MapGet("/oidc/csrf", Csrf);
        group.MapGet("/oidc/session", Session);
        group.MapPost("/oidc/logout", Logout);
        return app;
    }

    private static IResult Disabled() => Results.Json(
        new { success = false, message = AdminOidcSettings.DisabledError },
        statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Starts the authorization-code flow. The start guard has already validated the optional
    /// single-valued returnUrl (or answered the fixed 400 body); the challenge stores it in the
    /// protected state, so the completed sign-in redirects back to exactly that path. An
    /// unreachable authority answers the fixed identity-unavailable landing — the only failure the
    /// start endpoint can produce.
    /// </summary>
    private static async Task Start(HttpContext context)
    {
        var returnUrl = context.Request.Query["returnUrl"].ToString();
        try
        {
            await context.ChallengeAsync(AdminOidcConstants.OidcScheme, new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            context.Response.Redirect(AdminLoginResponseWriter.IdentityUnavailableRedirect);
        }
    }

    /// <summary>
    /// Issues the antiforgery credential pair: the cookie is set alongside the token, and issuance
    /// is a public, unauthenticated read — the protection is the cookie binding, not secrecy of
    /// the issuer. The declared shape is kept: <c>{"token": "..."}</c> with <c>no-store</c>.
    /// </summary>
    private static IResult Csrf(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(
            """{"token":""" + JsonSerializer.Serialize(tokens.RequestToken) + "}",
            "application/json");
    }

    /// <summary>
    /// Reports the live session's fixed status shape from the server-side ticket: never a token,
    /// never a reason other than authenticated/reauthenticationRequired.
    /// </summary>
    private static async Task Session(HttpContext context)
    {
        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        await AdminLoginResponseWriter.WriteSessionStatusAsync(context, session, context.RequestAborted);
    }

    /// <summary>
    /// The local-session-first logout with the standard RP-initiated upstream redirect:
    /// <list type="bullet">
    /// <item>A missing or wrong antiforgery credential answers the fixed 400
    /// <c>{"outcome":"csrf_rejected"}</c> and touches nothing.</item>
    /// <item>Without a live session there is nothing to revoke locally and no ID token to hint:
    /// the fixed local-only body.</item>
    /// <item>With one, the local session is revoked and the browser is handed to the authority's
    /// end-session endpoint through the stored ID token; a logout whose upstream redirect cannot
    /// be constructed still keeps the local revocation and answers the fixed local-only
    /// body.</item>
    /// </list>
    /// </summary>
    private static async Task Logout(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            // A missing or wrong token changes nothing: no session is touched and no upstream
            // redirect is attempted. The fixed answer is indistinguishable for both defect shapes.
            await WriteOutcomeAsync(context, StatusCodes.Status400BadRequest, "csrf_rejected");
            return;
        }

        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        if (!session.Succeeded)
        {
            // No live session means no local revocation and no end-session redirect: the fixed
            // local-only result, exactly as the package answered it.
            await WriteOutcomeAsync(context, StatusCodes.Status200OK, "local_only");
            return;
        }

        try
        {
            // Sign-out of the OpenID Connect scheme first: it reads the stored ID token from the
            // still-live session and sets the end-session redirect (id_token_hint, the registered
            // post-logout URI, and the protected state).
            await context.SignOutAsync(AdminOidcConstants.OidcScheme);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !context.RequestAborted.IsCancellationRequested)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            // The upstream redirect could not be constructed (for example the authority is
            // unreachable or publishes no end-session endpoint). The local session is still
            // revoked, never restored, and never retried within this request.
            await context.SignOutAsync(AdminOidcConstants.SessionScheme);
            if (!context.Response.HasStarted)
            {
                await WriteOutcomeAsync(context, StatusCodes.Status200OK, "local_only");
            }

            return;
        }

        // The local session ends after the redirect is set: the server-side ticket is removed and
        // the cookie deleted, and the 302 to the authority's end-session endpoint stands.
        await context.SignOutAsync(AdminOidcConstants.SessionScheme);
    }

    private static async Task WriteOutcomeAsync(HttpContext context, int statusCode, string outcome)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new { outcome }, context.RequestAborted);
    }
}
