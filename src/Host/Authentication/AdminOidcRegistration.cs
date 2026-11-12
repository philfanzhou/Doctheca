using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Registers the admin hosted-login slice on the framework's own OpenID Connect handler (the
/// official <c>SignaCore.Client.AspNetCore</c> client is in its deprecation window, ADR 0010). The
/// shared-framework handler owns the authorization-code flow, the standard RP-initiated logout,
/// and the correlation/nonce binding; this host owns the server-side session tickets, the CSRF
/// boundary, the admin pre-sign-in gate, the fixed response shapes, and the Bearer/session scheme
/// split — keeping the browser-facing contract of the replaced package integration unchanged.
/// </summary>
public static class AdminOidcRegistration
{
    public static IServiceCollection AddDocthecaAdminOidc(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = AdminOidcSettings.Read(configuration, environment);
        services.AddSingleton(settings);
        // Hosting diagnostics log raw query strings before the application middleware runs,
        // including callbacks rejected because hosted-login configuration is unavailable. The
        // framework's OpenID Connect handler logs the full protocol message URLs (authorization
        // code, state, and tokens included) at Debug level; the slice's security posture keeps
        // every protocol value out of host logs, so that category is silenced too.
        services.AddLogging(logging =>
        {
            logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
            logging.AddFilter("Microsoft.AspNetCore.Authentication.OpenIdConnect", LogLevel.None);
        });
        if (!settings.Available) return services;

        services.TryAddSingleton(TimeProvider.System);

        // The cookie profile follows the configured callback URI's scheme (transport is a
        // deployment decision made in front of the application): every carrier cookie is Secure
        // under the HTTPS profile and written without the Secure attribute under the plain-HTTP
        // profile, where a Secure cookie would be refused by the browser. The antiforgery cookie
        // keeps its Strict SameSite; the session cookie stays Lax so the browser sends it on the
        // top-level sign-in redirect chain.
        var plainHttpProfile = settings.RedirectUri.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AdminOidcConstants.CsrfCookie;
            options.HeaderName = AdminOidcConstants.CsrfHeader;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.IsEssential = true;
            options.Cookie.SecurePolicy = plainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        });

        // The backchannel carries the client secret and, through it, the authority's discovery,
        // JWKS, and token endpoints: it never follows a redirect, never carries a cookie, writes no
        // request/response log line, and normalizes client-side timeouts into transport failures
        // so a timeout keeps answering the fixed identity-unavailable outcome instead of a 5xx.
        services.AddTransient<AdminOidcBackchannelTimeoutHandler>();
        services.AddHttpClient(AdminOidcConstants.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false
            })
            .AddHttpMessageHandler<AdminOidcBackchannelTimeoutHandler>()
            .RemoveAllLoggers();

        // The in-process, capacity-bounded session ticket store and its periodic sweep.
        services.AddSingleton<AdminOidcTicketStore>();
        services.AddHostedService<AdminOidcTicketStoreCleanupService>();

        services.AddAuthentication()
            .AddCookie(AdminOidcConstants.SessionScheme, options =>
            {
                options.Cookie.Name = AdminOidcConstants.SessionCookie;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.IsEssential = true;
                options.Cookie.SecurePolicy = plainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                // The SPA navigates to the sign-in start explicitly, so the cookie handler's own
                // login redirect is never exercised and keeps its inert default. The ticket's
                // lifetime comes from the token response's expires_at (set at ticket receipt); the
                // fallback span below is only a defensive bound. Sessions never slide: an expired
                // session answers the fixed re-authentication result, never a silent refresh.
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = false;
            })
            .AddOpenIdConnect(AdminOidcConstants.OidcScheme, options =>
            {
                options.Authority = settings.Authority;
                options.ClientId = settings.ClientId;
                options.ClientSecret = settings.ClientSecret;
                options.ResponseType = OpenIdConnectResponseType.Code;
                // The code flow's standard response mode — and the only mode the authority
                // delivers. The framework's own default is form_post.
                options.ResponseMode = OpenIdConnectResponseMode.Query;
                options.UsePkce = true;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.SaveTokens = true;
                options.MapInboundClaims = false;
                options.CallbackPath = AdminOidcConstants.CallbackPath;
                options.SignedOutCallbackPath = AdminOidcConstants.LogoutReturnPath;
                options.SignedOutRedirectUri = AdminOidcConstants.SignedOutRedirect;
                options.SignInScheme = AdminOidcConstants.SessionScheme;
                // Plain-HTTP authorities are a deployment decision made in front of the
                // application; the framework requires this explicit opt-in.
                options.RequireHttpsMetadata = !plainHttpProfile;
                options.BackchannelTimeout = TimeSpan.FromSeconds(30);
                // The authorization parameters travel in the redirect itself; the authority's
                // discovery entry for PAR must never be taken.
                options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
                // The nonce and correlation cookies must survive the plain-HTTP profile: the
                // framework defaults (SameSite=None with Secure=Always) are refused by browsers
                // there. Lax is enough — the callback is a top-level navigation, exactly like the
                // session cookie itself.
                options.NonceCookie.SameSite = SameSiteMode.Lax;
                options.NonceCookie.SecurePolicy = plainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                options.CorrelationCookie.SecurePolicy = plainHttpProfile ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
                // The documented 30-second lifetime tolerance of the replaced integration.
                options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
                options.TokenValidationParameters.NameClaimType = "name";
                options.TokenValidationParameters.RoleClaimType = "role";

                // Strict authorization-response shape (kept from the replaced slice; the framework
                // handler itself accepts a weaker form): state/iss/code/error are single-valued,
                // the RFC 9207 issuer parameter is required, and it must equal the discovery
                // issuer — the mix-up protection of a single-authority client.
                options.Events.OnMessageReceived = async context =>
                {
                    foreach (var member in new[] { "state", "iss", "code", "error" })
                    {
                        if (context.Request.Query[member].Count > 1)
                        {
                            context.Fail(AdminOidcConstants.InvalidResponseFailure);
                            return;
                        }
                    }

                    // A response without a state or an issuer parameter is rejected before any
                    // authority interaction; a present issuer must then equal the discovery
                    // issuer. The comparison uses the cached configuration of the challenge.
                    var issuer = context.ProtocolMessage.Iss;
                    if (string.IsNullOrEmpty(context.ProtocolMessage.State)
                        || string.IsNullOrEmpty(issuer))
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                        return;
                    }

                    if (context.Options.ConfigurationManager is not { } manager
                        || await manager.GetConfigurationAsync(context.HttpContext.RequestAborted) is not { } configuration
                        || !string.Equals(issuer, configuration.Issuer, StringComparison.Ordinal))
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                    }
                };

                // The admin gate (the package's PreSignInAuthorizationDecision): a session may
                // only be established for a subject whose validated identity carries the admin
                // role. Failing validation prevents the sign-in entirely — no ticket and no cookie
                // are written, and an existing session is neither replaced nor extended. An
                // unsecured ID token is refused here as well: the framework skips the signature
                // requirement for tokens received over its authenticated backchannel.
                options.Events.OnTokenValidated = context =>
                {
                    if (string.Equals(context.SecurityToken?.SignatureAlgorithm, "none", StringComparison.Ordinal))
                    {
                        context.Fail(AdminOidcConstants.InvalidResponseFailure);
                        return Task.CompletedTask;
                    }

                    if (!context.Principal!.Claims.Any(claim =>
                            claim.Type is "role" or ClaimTypes.Role
                            && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)))
                    {
                        context.Fail(AdminOidcConstants.NotAdminFailure);
                    }

                    return Task.CompletedTask;
                };

                // Every protocol failure lands on one bounded SPA outcome; no failure detail ever
                // reaches the browser.
                options.Events.OnRemoteFailure = context =>
                {
                    context.Response.Redirect(AdminLoginResponseWriter.RedirectTarget(context.Failure));
                    context.HandleResponse();
                    return Task.CompletedTask;
                };

                // The server-side session never outlives the issued access token, and the cookie
                // is persistent with the same boundary so a browser restart keeps neither.
                options.Events.OnTicketReceived = context =>
                {
                    if (context.Properties is { } properties
                        && DateTimeOffset.TryParse(
                            properties.GetTokenValue("expires_at"), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var expiry))
                    {
                        properties.ExpiresUtc = expiry.ToUniversalTime();
                        properties.IsPersistent = true;
                    }

                    return Task.CompletedTask;
                };

                // The standard RP-initiated logout: the end-session redirect carries the configured
                // post-logout URI exactly as registered with the authority (never a
                // request-scheme-derived one, which a TLS-terminating proxy would break), and the
                // protected state lands the browser on the fixed signed-out SPA result.
                options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                {
                    context.ProtocolMessage.PostLogoutRedirectUri = settings.PostLogoutRedirectUri;
                    context.Properties.RedirectUri = AdminOidcConstants.SignedOutRedirect;
                    return Task.CompletedTask;
                };

                // A sign-out callback without an unprotectable state is not a completed logout:
                // answer the fixed 400 page instead of an empty 200.
                options.Events.OnSignedOutCallbackRedirect = async context =>
                {
                    if (!string.IsNullOrEmpty(context.Properties?.RedirectUri))
                    {
                        return;
                    }

                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    await context.Response.WriteAsync(
                        """
                        <!doctype html>
                        <html lang="en">
                        <head><title>Sign-out could not be confirmed</title></head>
                        <body>
                        <h1>Sign-out could not be confirmed</h1>
                        <p>The sign-out result could not be matched. Sign in again if you were trying to use the application.</p>
                        <p><a href="/">Back</a></p>
                        </body>
                        </html>
                        """,
                        context.HttpContext.RequestAborted);
                    context.HandleResponse();
                };
            });

        // The DI TimeProvider (the test suite's manual clock) drives both handlers' expiry
        // decisions, and the OpenID Connect backchannel is the hardened named client above.
        services.AddOptions<CookieAuthenticationOptions>(AdminOidcConstants.SessionScheme)
            .Configure<AdminOidcTicketStore, TimeProvider>((options, store, time) =>
            {
                options.SessionStore = store;
                options.TimeProvider = time;
            });
        services.AddOptions<OpenIdConnectOptions>(AdminOidcConstants.OidcScheme)
            .Configure<TimeProvider, IHttpClientFactory>((options, time, factory) =>
            {
                options.TimeProvider = time;
                options.Backchannel = factory.CreateClient(AdminOidcConstants.HttpClientName);
            });

        // The forwarding scheme the admin policy authenticates against: it routes a request to
        // the host's JwtBearer handler exactly when the session boundary has independently
        // verified the Bearer credential (see AdminOidcSessionMiddleware), and to the cookie
        // session everywhere else. A present but invalid Bearer header therefore never steals the
        // session's role claims. Challenges keep the fixed 401 contract instead of redirecting
        // anonymous API calls to the sign-in start.
        services.AddAuthentication()
            .AddPolicyScheme(AdminOidcConstants.AuthenticationScheme, "Doctheca admin hosted login", options =>
            {
                options.ForwardDefaultSelector = context =>
                    context.Items.ContainsKey(AdminOidcSessionMiddleware.VerifiedBearerItemKey)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : AdminOidcConstants.SessionScheme;
                options.ForwardChallenge = JwtBearerDefaults.AuthenticationScheme;
            });
        return services;
    }
}
