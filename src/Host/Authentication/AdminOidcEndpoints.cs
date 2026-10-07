using SignaCore.Client.AspNetCore;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The hosted-login endpoints. Missing configuration returns fixed 503 responses; with a
/// complete configuration the official package's endpoints are mounted under the same prefix
/// and inherit this host's anonymous-access and security-header markers through an empty-prefix
/// route group.
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

        // The empty-prefix group applies this host's endpoint markers (fallback-policy opt-out
        // and the ServiceMantle security response headers) to every package-mapped route while
        // the package keeps validating the redirect URI against the concrete prefix.
        app.MapGroup(string.Empty)
            .RequireServiceMantleSecurityResponseHeaders()
            .AllowAnonymous()
            .MapSignaCoreHostedLogin(AdminOidcConstants.Prefix);
        return app;
    }

    private static IResult Disabled() => Results.Json(
        new { success = false, message = AdminOidcSettings.DisabledError },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
