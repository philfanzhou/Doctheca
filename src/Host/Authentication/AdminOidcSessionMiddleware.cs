using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Net.Http.Headers;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Request boundary for cookie-session access to the protected admin API (hosted login). It keeps
/// the replaced slice's fixed results: an expired or unknown session answers the fixed
/// re-authentication JSON before any endpoint runs, and a session-authenticated unsafe method
/// without a valid antiforgery credential answers the fixed CSRF JSON.
/// </summary>
/// <remarks>
/// <para>
/// Only requests that actually carry the opaque session cookie enter the boundary, and only under
/// <c>/admin</c> outside the anonymous <c>/admin/auth</c> area. A Bearer header is served by the
/// boundary only after the host's JwtBearer handler independently verifies it; the verified marker
/// also routes the admin policy's forwarding scheme to JwtBearer, so a Bearer principal can never
/// borrow the cookie session's role claims. A present but unverified Bearer header falls back to
/// the session boundary exactly as before. Bearer-authenticated requests pass without any CSRF
/// requirement.
/// </para>
/// <para>
/// The CSRF validation itself runs here for every session-authenticated unsafe method (RFC 9110
/// safe methods are GET, HEAD, OPTIONS, and TRACE): the antiforgery pair issued by the
/// <c>/admin/auth/oidc/csrf</c> endpoint must validate, otherwise the fixed 403 is answered and
/// the endpoint never runs. The response bodies are fixed and contain nothing from the request;
/// the ServiceMantle security response headers come from the endpoint markers, not from this
/// middleware.
/// </para>
/// </remarks>
public sealed class AdminOidcSessionMiddleware(RequestDelegate next)
{
    internal const string VerifiedBearerItemKey = "doctheca.oidc.verified_bearer";
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
                && authorization.Count == 1
                && authorization[0]?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Header presence is not authentication. Only the independently verified Bearer
                // credential may bypass the Cookie CSRF boundary and carry its own identity.
                var bearer = await context.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (bearer.Succeeded)
                {
                    context.Items[VerifiedBearerItemKey] = true;
                    context.User = bearer.Principal!;
                    await next(context);
                    return;
                }
            }

            var session = await context.AuthenticateAsync(AdminOidcConstants.SessionScheme);
            if (session.Succeeded)
            {
                if (!IsSafeMethod(context.Request.Method))
                {
                    try
                    {
                        await context.RequestServices.GetRequiredService<IAntiforgery>()
                            .ValidateRequestAsync(context);
                    }
                    catch (AntiforgeryValidationException)
                    {
                        // The request is not silently re-authenticated: one fixed 403, and the
                        // endpoint never runs.
                        await RejectAsync(context, StatusCodes.Status403Forbidden, CsrfRequired);
                        return;
                    }
                }

                context.User = session.Principal!;
            }
            else
            {
                // Unknown, expired, and unprotectable sessions are indistinguishable: the fixed
                // re-authentication answer.
                await RejectAsync(context, StatusCodes.Status401Unauthorized, ReauthenticationRequired);
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// The RFC 9110 safe methods: GET, HEAD, OPTIONS, and TRACE. Everything else — POST, PUT,
    /// PATCH, DELETE, and any extension method — must carry the antiforgery credential.
    /// </summary>
    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || HttpMethods.IsTrace(method);

    private static Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { success = false, message }, context.RequestAborted);
    }
}
