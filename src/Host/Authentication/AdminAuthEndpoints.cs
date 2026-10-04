using System.Security.Claims;
using Doctheca.Service;

namespace Doctheca.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth")
            .RequireServiceMantleSecurityResponseHeaders();
        // No body binding: every legacy request receives the same retirement response.
        group.MapPost("/login", Retired).AllowAnonymous();
        group.MapPost("/refresh", Retired).AllowAnonymous();
        group.MapPost("/logout", Retired).AllowAnonymous();
        group.MapGet("/session", GetSession)
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin);
        return app;
    }

    private static IResult Retired() => Results.Json(
        new { success = false, message = "Password authentication has been retired. Use hosted sign-in." },
        statusCode: StatusCodes.Status410Gone);

    private static IResult GetSession(ClaimsPrincipal user)
    {
        var roles = user.Claims
            .Where(claim => claim.Type is "role" or ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expiresAt = long.TryParse(user.FindFirst("exp")?.Value, out var expiry)
            ? expiry
            : 0;

        return Results.Ok(new
        {
            success = true,
            data = new
            {
                userId = FindFirst(user, "sub", ClaimTypes.NameIdentifier),
                username = FindFirst(user, "unique_name", "name", ClaimTypes.Name),
                roles,
                expiresAt
            }
        });
    }

    private static string FindFirst(ClaimsPrincipal principal, params string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

}
