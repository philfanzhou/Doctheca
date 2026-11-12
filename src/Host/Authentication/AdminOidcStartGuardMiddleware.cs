namespace Doctheca.Host.Authentication;

/// <summary>
/// Keeps the strict sign-in-entry input contract: only the single-valued <c>returnUrl</c> key is
/// accepted and its value must satisfy <see cref="AdminOidcSettings.IsValidReturnUrl"/> — before
/// the framework's OpenID Connect challenge runs (the handler itself accepts a weaker shape and
/// ignores unknown query keys). An invalid input answers the same fixed 400 body and never starts
/// a pending sign-in.
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
                await AdminLoginResponseWriter.WriteInvalidReturnUrlAsync(context, context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
