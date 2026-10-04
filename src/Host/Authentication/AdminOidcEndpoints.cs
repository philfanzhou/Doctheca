using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The hosted-login endpoints. Missing configuration returns fixed 503 responses.
/// All carry the ServiceMantle security response-header baseline through the shared
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
        group.MapGet("/oidc/session", SessionAsync).AllowAnonymous();
        group.MapPost("/oidc/logout", AdminOidcLogout.Logout).AllowAnonymous();
        group.MapGet("/oidc/logout/return", AdminOidcLogout.Return).AllowAnonymous();

        return app;
    }

    private static async Task<IResult> StartAsync(
        string? returnUrl,
        HttpContext context,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions)
    {
        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (!settings.Available)
        {
            return Disabled();
        }

        // A missing or empty returnUrl targets the SPA root; only explicitly invalid values
        // answer the fixed 400 below.
        if (context.Request.Query.Keys.Any(key => key != "returnUrl")
            || context.Request.Query.TryGetValue("returnUrl", out var values) && values.Count != 1
            || !string.IsNullOrEmpty(returnUrl) && !AdminOidcSettings.IsValidReturnUrl(returnUrl))
        {
            return Results.Json(new { success = false, message = "The return URL must be a path inside this site." }, statusCode: 400);
        }
        if (string.IsNullOrEmpty(returnUrl))
        {
            returnUrl = "/";
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
        if (!settings.Available)
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
        return Results.Ok(new { success = true, data = new { requestToken = tokens.RequestToken } });
    }

    private static async Task<IResult> SessionAsync(HttpContext context, AdminOidcSettings settings)
    {
        if (!settings.Available) return Disabled();
        var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
        return session.Succeeded
            ? Results.Ok(new { success = true, data = new { authenticated = true, reason = "authenticated", username = session.Principal!.Identity!.Name, expiresAt = session.Properties!.ExpiresUtc } })
            : Results.Json(new { success = false, message = AdminOidcSessionMiddleware.ReauthenticationRequired, data = new { authenticated = false, reason = "reauthenticationRequired" } }, statusCode: 401);
    }

    internal static IResult Disabled() => Results.Json(
        new { success = false, message = AdminOidcSettings.DisabledError },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
