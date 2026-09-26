using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Doctheca.Service;

namespace Doctheca.Host.Authentication;

public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapGet("/session", GetSession)
            .RequireAuthorization(DocthecaAuthorizationPolicies.Admin);

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext context,
        IIdentityAuthenticationService identityService,
        IIdentityTokenValidator tokenValidator,
        IOptions<DocthecaCookieOptions> cookieOptions,
        ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new
            {
                success = false,
                message = "Username and password are required."
            });
        }

        var exchange = await identityService.PasswordGrantAsync(
            request.Username.Trim(),
            request.Password,
            context.RequestAborted);

        return await CompleteLoginAsync(
            exchange,
            context,
            tokenValidator,
            cookieOptions.Value,
            loggerFactory.CreateLogger(nameof(AdminAuthEndpoints)));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext context,
        IIdentityAuthenticationService identityService,
        IIdentityTokenValidator tokenValidator,
        IOptions<DocthecaCookieOptions> cookieOptions,
        ILoggerFactory loggerFactory)
    {
        var options = cookieOptions.Value;
        if (!context.Request.Cookies.TryGetValue(
                DocthecaAuthenticationConstants.RefreshCookieName,
                out var refreshToken)
            || string.IsNullOrWhiteSpace(refreshToken))
        {
            ClearCookies(context.Response, options);
            return Unauthorized("Authentication is required.");
        }

        var exchange = await identityService.RefreshAsync(refreshToken, context.RequestAborted);
        if (exchange.Status != IdentityExchangeStatus.Succeeded)
        {
            ClearCookies(context.Response, options);
            return exchange.Status == IdentityExchangeStatus.Unavailable
                ? BadGateway("Identity service is unavailable.")
                : Unauthorized("Authentication is required.");
        }

        try
        {
            var validated = await tokenValidator.ValidateTokenAsync(
                exchange.AccessToken,
                context.RequestAborted);
            if (!validated.IsAdministrator)
            {
                ClearCookies(context.Response, options);
                return Unauthorized("Authentication is required.");
            }

            AppendCookies(context.Response, exchange, options);
            return Results.Ok(new
            {
                success = true,
                data = ToSessionData(validated)
            });
        }
        catch (Exception ex) when (ex is SecurityTokenException or InvalidOperationException)
        {
            loggerFactory.CreateLogger(nameof(AdminAuthEndpoints))
                .LogWarning("Identity refresh returned an invalid access token");
            ClearCookies(context.Response, options);
            return Unauthorized("Authentication is required.");
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger(nameof(AdminAuthEndpoints))
                .LogWarning("Identity refresh validation failed: {ExceptionType}", ex.GetType().Name);
            ClearCookies(context.Response, options);
            return BadGateway("Identity service is unavailable.");
        }
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IIdentityAuthenticationService identityService,
        IOptions<DocthecaCookieOptions> cookieOptions,
        ILoggerFactory loggerFactory)
    {
        if (context.Request.Cookies.TryGetValue(
                DocthecaAuthenticationConstants.RefreshCookieName,
                out var refreshToken)
            && !string.IsNullOrWhiteSpace(refreshToken))
        {
            try
            {
                await identityService.RevokeAsync(refreshToken, context.RequestAborted);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger(nameof(AdminAuthEndpoints))
                    .LogWarning("Identity refresh-token revocation failed: {ExceptionType}", ex.GetType().Name);
            }
        }

        ClearCookies(context.Response, cookieOptions.Value);
        return Results.Ok(new { success = true });
    }

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

    private static async Task<IResult> CompleteLoginAsync(
        IdentityTokenExchangeResult exchange,
        HttpContext context,
        IIdentityTokenValidator tokenValidator,
        DocthecaCookieOptions cookieOptions,
        ILogger logger)
    {
        if (exchange.Status == IdentityExchangeStatus.Rejected)
        {
            return Unauthorized("Invalid username or password.");
        }

        if (exchange.Status == IdentityExchangeStatus.Unavailable)
        {
            return BadGateway("Identity service is unavailable.");
        }

        if (exchange.Status != IdentityExchangeStatus.Succeeded)
        {
            return BadGateway("Identity service returned an invalid token.");
        }

        try
        {
            var validated = await tokenValidator.ValidateTokenAsync(
                exchange.AccessToken,
                context.RequestAborted);
            if (!validated.IsAdministrator)
            {
                return Results.Json(
                    new { success = false, message = "Administrator access is required." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            AppendCookies(context.Response, exchange, cookieOptions);
            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    validated.Username,
                    validated.Roles,
                    validated.ExpiresAt
                }
            });
        }
        catch (Exception ex) when (ex is SecurityTokenException or InvalidOperationException)
        {
            logger.LogWarning("Identity login returned an invalid access token");
            return BadGateway("Identity service returned an invalid token.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Identity login token validation failed: {ExceptionType}",
                ex.GetType().Name);
            return BadGateway("Identity service is unavailable.");
        }
    }

    private static void AppendCookies(
        HttpResponse response,
        IdentityTokenExchangeResult exchange,
        DocthecaCookieOptions options)
    {
        response.Cookies.Append(
            DocthecaAuthenticationConstants.AccessCookieName,
            exchange.AccessToken,
            CreateCookieOptions("/admin", options.CookieSecure, exchange.ExpiresAt));
        response.Cookies.Append(
            DocthecaAuthenticationConstants.RefreshCookieName,
            exchange.RefreshToken,
            CreateCookieOptions("/admin/auth", options.CookieSecure));
    }

    private static void ClearCookies(HttpResponse response, DocthecaCookieOptions options)
    {
        response.Cookies.Delete(
            DocthecaAuthenticationConstants.AccessCookieName,
            CreateCookieOptions("/admin", options.CookieSecure));
        response.Cookies.Delete(
            DocthecaAuthenticationConstants.RefreshCookieName,
            CreateCookieOptions("/admin/auth", options.CookieSecure));
    }

    private static CookieOptions CreateCookieOptions(
        string path,
        bool secure,
        long? expiresAt = null)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = secure,
            Path = path,
            Expires = expiresAt.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value)
                : null
        };
    }

    private static IResult Unauthorized(string message) => Results.Json(
        new { success = false, message },
        statusCode: StatusCodes.Status401Unauthorized);

    private static IResult BadGateway(string message) => Results.Json(
        new { success = false, message },
        statusCode: StatusCodes.Status502BadGateway);

    private static object ToSessionData(ValidatedIdentityToken token) => new
    {
        token.UserId,
        token.Username,
        token.Roles,
        token.ExpiresAt
    };

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

    public sealed record LoginRequest(string Username, string Password);
}
