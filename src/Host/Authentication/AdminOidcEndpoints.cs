using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The three hosted-login endpoints (issue #47). While <c>AdminOidc:Enabled</c> is false each
/// answers a fixed 503; the rest of the auth area keeps the legacy password flow untouched.
/// All three carry the ServiceMantle security response-header baseline through the shared
/// route-group marker, including the framework-handled callback redirect.
/// </summary>
public static class AdminOidcEndpoints
{
    internal const string IdentityUnavailableRedirect = "/?authError=identityUnavailable";

    public static WebApplication MapAdminOidcEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth")
            .RequireServiceMantleSecurityResponseHeaders();

        group.MapGet("/oidc/start", StartAsync).AllowAnonymous();
        // Registered so the callback path carries endpoint metadata (security headers, fixed
        // 503 while disabled). While enabled the OIDC handler short-circuits it first.
        group.MapGet("/oidc/callback", Callback).AllowAnonymous();
        group.MapGet("/oidc/csrf", CsrfAsync).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> StartAsync(
        string? returnUrl,
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions)
    {
        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (!settings.Enabled)
        {
            return Disabled();
        }

        // A missing or empty returnUrl targets the SPA root; only explicitly invalid values
        // answer the fixed 400 below.
        if (string.IsNullOrEmpty(returnUrl))
        {
            returnUrl = "/";
        }
        else if (!AdminOidcSettings.IsValidReturnUrl(returnUrl))
        {
            return Results.Json(
                new { success = false, message = "The return URL must be a path inside this site." },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var options = oidcOptions.Get(AdminOidcConstants.OidcScheme);
        try
        {
            await AdminOidcRegistration.Metadata(options, settings, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Results.Redirect(IdentityUnavailableRedirect);
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        await context.ChallengeAsync(AdminOidcConstants.OidcScheme, new AuthenticationProperties
        {
            RedirectUri = returnUrl
        });
        return Results.Empty;
    }

    private static IResult Callback(HttpContext context)
    {
        // Unreachable while enabled: the OIDC handler owns the callback path.
        return Disabled();
    }

    private static async Task<IResult> CsrfAsync(HttpContext context, IAntiforgery antiforgery)
    {
        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (!settings.Enabled)
        {
            return Disabled();
        }

        // The request token is handed out only to a live server-side session. Doing this in
        // the handler (instead of RequireAuthorization) keeps the fixed 503 while disabled.
        // The principal is bound before minting: the antiforgery tokens embed the identity
        // they were issued to, which is the session principal the boundary restores later.
        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        if (!session.Succeeded)
        {
            return Results.Json(
                new { success = false, message = AdminOidcSessionMiddleware.ReauthenticationRequired },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        context.User = session.Principal!;
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Ok(new { success = true, requestToken = tokens.RequestToken });
    }

    private static IResult Disabled() => Results.Json(
        new { success = false, message = AdminOidcSettings.DisabledError },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
