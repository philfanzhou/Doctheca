using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Net.Http.Headers;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Request boundary for cookie-session access to the protected admin API (issue #47).
/// </summary>
/// <remarks>
/// <para>
/// Only requests that actually carry the opaque session cookie enter the boundary, and only
/// under <c>/admin</c> outside the anonymous <c>/admin/auth</c> area. An expired or unknown
/// session answers the fixed re-authentication result and never reaches the endpoint, so no
/// downstream call is made and no write is replayed. A live session must present the CSRF
/// credential for non-safe methods. Bearer callers (Authorization header) never enter the
/// boundary results: they keep the exact legacy behavior.
/// </para>
/// <para>
/// The response bodies are fixed and contain nothing from the request; the ServiceMantle
/// security response headers come from the endpoint markers, not from this middleware.
/// </para>
/// </remarks>
public sealed class AdminOidcSessionMiddleware(RequestDelegate next)
{
    internal const string BearerOnly = "doctheca.oidc.bearer_only";
    internal const string ReauthenticationRequired = "Re-authentication is required.";
    internal const string CsrfRequired = "A valid anti-forgery token is required.";

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<AdminOidcSettings>();
        if (settings.Available
            && context.Request.Path.StartsWithSegments("/admin")
            && !context.Request.Path.StartsWithSegments("/admin/auth")
            && context.Request.Cookies.TryGetValue(AdminOidcConstants.SessionCookie, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            if (context.Request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization)
                && authorization.Count == 1 && authorization[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Header presence is not authentication. Only the independently verified
                // Bearer credential may bypass the Cookie CSRF boundary.
                var bearer = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (bearer.Succeeded)
                { context.Items[BearerOnly] = true; context.User = bearer.Principal!; await next(context); return; }
            }
            var result = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
            if (!result.Succeeded)
            {
                await RejectAsync(context, StatusCodes.Status401Unauthorized, ReauthenticationRequired);
                return;
            }

            // Bind the session principal before the CSRF check: the antiforgery tokens embed
            // the identity they were issued to, and authorization runs later on its own
            // multi-scheme authentication.
            context.User = result.Principal!;

            var method = context.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method))
            {
                if (!context.Request.Headers.TryGetValue(AdminOidcConstants.CsrfHeader, out var header)
                    || header.Count != 1 || string.IsNullOrWhiteSpace(header[0]))
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden, CsrfRequired);
                    return;
                }
                try
                {
                    await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
                }
                catch (AntiforgeryValidationException)
                {
                    await RejectAsync(context, StatusCodes.Status403Forbidden, CsrfRequired);
                    return;
                }
            }
        }

        await next(context);
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { success = false, message }, context.RequestAborted);
    }
}
