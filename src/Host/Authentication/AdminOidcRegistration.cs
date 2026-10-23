using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SignaCore.Client.AspNetCore;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Registers the SignaCore hosted-login slice through the official client package (issue #70).
/// The package owns the OIDC code flow, the server-side session tickets, the CSRF boundary, and
/// the prepared logout; this host adds only the adaptation surface: the availability precheck,
/// the admin pre-sign-in gate, the fixed response shapes, and the Bearer/session scheme split.
/// </summary>
public static class AdminOidcRegistration
{
    /// <summary>The bounded capacity of the in-process ticket store, unchanged from the
    /// replaced self-written slice.</summary>
    internal const int TicketCapacity = 4096;

    public static IServiceCollection AddDocthecaAdminOidc(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = AdminOidcSettings.Read(configuration, environment);
        services.AddSingleton(settings);
        // Hosting diagnostics log raw query strings before the application middleware runs,
        // including callbacks rejected because hosted-login configuration is unavailable.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None));
        if (!settings.Available) return services;

        services.TryAddSingleton(TimeProvider.System);
        services.AddSignaCoreHostedLogin(options =>
        {
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.RedirectUri = settings.RedirectUri;
            options.Scope = "openid profile";
            // Preserve the documented 30-second lifetime tolerance. Keep future-iat rejection
            // enabled: it also protects the access token, whose old validator enforced it.
            // Scope, duplicate-JSON and response-size checks keep their strict defaults too.
            options.Validation.ClockSkew = TimeSpan.FromSeconds(30);
            options.SessionCookieName = AdminOidcConstants.SessionCookie;
            options.AntiforgeryHeaderName = AdminOidcConstants.CsrfHeader;
            options.TicketCapacity = TicketCapacity;
            options.PreSignInAuthorizationDecision = AdminPreSignInAuthorizationDecision.Instance;
            options.ResponseWriter = AdminLoginResponseWriter.Instance;
            options.SchemeSelector = SelectScheme;
            options.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
            options.PostLogoutReturnPath = AdminOidcConstants.SignedOutRedirect;
        });
        // Keep the public cookie name; the package owns the Secure policy derived from the
        // configured callback scheme, independently of the environment and request scheme.
        services.AddOptions<AntiforgeryOptions>()
            .PostConfigure(options => options.Cookie.Name = AdminOidcConstants.CsrfCookie);
        // The SPA consumes the protected API with explicit sign-in, so challenges must keep the
        // fixed 401 contract instead of redirecting anonymous API calls to the sign-in start.
        services.AddOptions<PolicySchemeOptions>(SignaCoreHostedLoginDefaults.AuthenticationScheme)
            .Configure(options => options.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme);
        // Backchannel timeouts keep the fixed identity-unavailable outcome (see the handler).
        services.AddTransient<AdminOidcBackchannelTimeoutHandler>();
        services.AddHttpClient(SignaCoreHostedLoginDefaults.HttpClientName)
            .AddHttpMessageHandler<AdminOidcBackchannelTimeoutHandler>();
        return services;
    }

    /// <summary>
    /// Routes the package's forwarding scheme to the host's JwtBearer handler exactly when the
    /// session boundary has independently verified the Bearer credential. A present but invalid
    /// Bearer header therefore never steals the session's role claims, while the unverified
    /// fallback (session) stays available everywhere else.
    /// </summary>
    internal static string? SelectScheme(HttpContext context) =>
        context.Items.ContainsKey(AdminOidcSessionMiddleware.VerifiedBearerItemKey)
            ? JwtBearerDefaults.AuthenticationScheme
            : null;
}
