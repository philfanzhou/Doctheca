namespace Doctheca.Host.Authentication;

/// <summary>
/// Keeps the strict sign-in-entry input contract of the replaced self-written slice (issue #70):
/// the official package's start endpoint accepts a weaker <c>returnUrl</c> shape and ignores
/// unknown query keys, so this guard applies the exact prior rules — only the single-valued
/// <c>returnUrl</c> key is accepted and its value must satisfy
/// <see cref="AdminOidcSettings.IsValidReturnUrl"/> — before the package endpoint runs. An
/// invalid input answers the same fixed 400 body and never creates a pending sign-in.
/// </summary>
public sealed class AdminOidcStartGuardMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path == AdminOidcConstants.Prefix + "/start"
            && context.RequestServices.GetRequiredService<AdminOidcSettings>().Available)
        {
            var query = context.Request.Query;
            var valid = query.Keys.All(key => key == "returnUrl");
            if (valid && query.TryGetValue("returnUrl", out var values))
                valid = values.Count == 1 && AdminOidcSettings.IsValidReturnUrl(values[0]);
            if (!valid)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new { success = false, message = AdminLoginResponseWriter.InvalidReturnUrlMessage },
                    context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
