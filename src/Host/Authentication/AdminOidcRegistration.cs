using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Registers the SignaCore hosted-login slice (issue #47): server-side session tickets, the
/// authorization-code callback, and the CSRF boundary for cookie-authenticated API writes.
/// </summary>
public static class AdminOidcRegistration
{
    /// <summary>
    /// Fixed failure-redirect targets. Every callback failure funnels into one of these
    /// same-site locations; none of them echoes protocol parameters.
    /// </summary>
    internal const string SignInFailedRedirect = "/?authError=signInFailed";
    internal const string DeniedRedirect = "/?authError=notAdmin";
    internal const string CancelledRedirect = "/?authError=cancelled";

    public static IServiceCollection AddDocthecaAdminOidc(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var settings = AdminOidcSettings.Read(configuration, environment);
        services.AddSingleton(settings);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CompactStateDataFormat>();
        services.AddSingleton<MemoryTicketStore>();
        services.AddSingleton<LogoutReturnStore>();
        services.AddHostedService<OidcStoreCleanup>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = AdminOidcConstants.CsrfHeader;
            options.Cookie.Name = AdminOidcConstants.CsrfCookie;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = settings.InsecureLoopback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
        // The session cookie is registered unconditionally so the authorization policy keeps a
        // stable scheme list; without configuration no session can be issued.
        var authentication = services.AddAuthentication().AddCookie(AdminOidcConstants.SessionScheme, options =>
        {
            options.Cookie.Name = AdminOidcConstants.SessionCookie;
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = settings.InsecureLoopback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = MemoryTicketStore.Lifetime;
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.HttpContext.Items.ContainsKey(AdminOidcSessionMiddleware.BearerOnly)) context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
        services.AddOptions<CookieAuthenticationOptions>(AdminOidcConstants.SessionScheme)
            .Configure<MemoryTicketStore, TimeProvider>((options, store, time) => { options.SessionStore = store; options.TimeProvider = time; });
        // Hosting diagnostics log raw query strings before the application middleware runs,
        // including callbacks rejected because hosted-login configuration is unavailable.
        services.AddLogging(logging => logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None));
        if (!settings.Available) return services;

        authentication.AddOpenIdConnect(AdminOidcConstants.OidcScheme, options =>
        {
            options.BackchannelHttpHandler = new HttpClientHandler { AllowAutoRedirect = false };
            options.BackchannelTimeout = TimeSpan.FromSeconds(10);
            options.SignInScheme = AdminOidcConstants.SessionScheme;
            options.Authority = settings.Authority;
            options.ClientId = settings.ClientId;
            options.ClientSecret = settings.ClientSecret;
            options.CallbackPath = AdminOidcConstants.CallbackPath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.SaveTokens = true;
            options.UseTokenLifetime = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.ClaimActions.Clear();
            options.DisableTelemetry = true;
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.RequireHttpsMetadata = settings.Authority.StartsWith("https://", StringComparison.Ordinal);
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            options.RemoteAuthenticationTimeout = CompactStateDataFormat.Lifetime;
            foreach (var cookie in new[] { options.NonceCookie, options.CorrelationCookie })
            {
                cookie.HttpOnly = true;
                cookie.SameSite = SameSiteMode.Lax;
                cookie.SecurePolicy = settings.InsecureLoopback ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                cookie.MaxAge = CompactStateDataFormat.Lifetime;
            }
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = settings.Authority,
                ValidateAudience = true, ValidAudience = settings.ClientId,
                RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = settings.ClockSkew,
                NameClaimType = "unique_name", RoleClaimType = "oidc.roles.not_authorized"
            };
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    context.ProtocolMessage.RedirectUri = settings.RedirectUri;
                    return Task.CompletedTask;
                },
                OnMessageReceived = async context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    var query = context.Request.Query;
                    static bool One(IQueryCollection q, string key) => q.TryGetValue(key, out var values)
                        && values.Count == 1 && !string.IsNullOrWhiteSpace(values[0]);
                    if (!HttpMethods.IsGet(context.Request.Method) || !One(query, "state") || !One(query, "iss")
                        || context.Properties is null || query.ContainsKey("id_token") || query.ContainsKey("access_token")
                        || (query.ContainsKey("error") ? !One(query, "error") || query.ContainsKey("code") : !One(query, "code")))
                    { context.Fail("oidc.invalid_callback"); return; }
                    try
                    {
                        var metadata = await Metadata(context.Options, settings, context.HttpContext.RequestAborted);
                        if (!string.Equals(query["iss"][0], metadata.Issuer, StringComparison.Ordinal)) context.Fail("oidc.invalid_callback");
                    }
                    catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
                    catch { context.Fail("oidc.invalid_callback"); }
                },
                OnTokenResponseReceived = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    var token = context.TokenEndpointResponse;
                    if (string.IsNullOrEmpty(token.AccessToken) || string.IsNullOrEmpty(token.IdToken)
                        || token.TokenType != "Bearer" || !int.TryParse(token.ExpiresIn, out var expires) || expires <= 0
                        || !string.IsNullOrEmpty(token.RefreshToken)) context.Fail("oidc.invalid_token_response");
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    var subjects = context.Principal?.FindAll("sub").ToArray() ?? [];
                    if (subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0].Value)
                        || context.SecurityToken.Issuer != settings.Authority)
                    { context.Fail("oidc.invalid_id_token"); return; }
                    // The session is minted from the validated access token, not from the ID
                    // token: the admin policy asserts role=admin, and only a fully verified
                    // access token carrying that role may sign in.
                    var accessToken = context.TokenEndpointResponse!.AccessToken;
                    if (!StrictIdTokenHandler.TrySubject(context.TokenEndpointResponse.IdToken, out var idIssuer, out var idSubject)
                        || !StrictIdTokenHandler.TrySubject(accessToken, out var accessIssuer, out var accessSubject)
                        || idIssuer != accessIssuer || idSubject != accessSubject)
                    { context.Fail("oidc.invalid_subject_binding"); return; }
                    TokenValidationResult access;
                    try
                    {
                        var metadata = await Metadata(context.Options, settings, context.HttpContext.RequestAborted);
                        var parameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true, ValidIssuer = settings.Authority,
                            ValidateAudience = true, ValidAudience = settings.ClientId,
                            RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                            IssuerSigningKeys = metadata.SigningKeys,
                            RequireExpirationTime = true, ValidateLifetime = true, ClockSkew = settings.ClockSkew,
                            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                            ValidTypes = ["at+jwt"]
                        };
                        access = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, parameters);
                    }
                    catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
                    catch { context.Fail("oidc.invalid_access_token"); return; }
                    if (!access.IsValid) { context.Fail("oidc.invalid_access_token"); return; }
                    if (!access.ClaimsIdentity.HasClaim(claim =>
                            claim.Type is "role" or ClaimTypes.Role
                            && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)))
                    { context.Fail("oidc.not_admin"); return; }
                    context.Properties!.Items["oidc.issuer"] = context.SecurityToken.Issuer;
                    context.Properties.Items["oidc.subject"] = subjects[0].Value;
                    // Carry the verified access-token expiry so the ticket clock is capped by it.
                    if (access.ClaimsIdentity.FindFirst("exp") is { } expiry
                        && long.TryParse(expiry.Value, out var seconds))
                    {
                        context.Properties.Items["oidc.access_expires"] = seconds.ToString(CultureInfo.InvariantCulture);
                    }
                    context.Principal = new ClaimsPrincipal(new ClaimsIdentity(
                        access.ClaimsIdentity.Claims, AdminOidcConstants.SessionScheme, "unique_name", "role"));
                },
                OnTicketReceived = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    // The session never outlives the access token: the deadline is the verified
                    // access-token expiry (capped by the store's absolute lifetime), and the
                    // cookie carries the same instant.
                    var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
                    var expires = now + MemoryTicketStore.Lifetime;
                    if (context.Properties!.Items.TryGetValue("oidc.access_expires", out var raw)
                        && long.TryParse(raw, out var seconds))
                    {
                        var access = DateTimeOffset.FromUnixTimeSeconds(seconds);
                        if (access < expires) expires = access;
                    }
                    context.Properties.IssuedUtc = now;
                    context.Properties.ExpiresUtc = expires;
                    context.Properties.AllowRefresh = false;
                    return Task.CompletedTask;
                },
                OnAccessDenied = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    context.Response.Redirect(CancelledRedirect); context.HandleResponse(); return Task.CompletedTask;
                },
                OnRemoteFailure = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    context.Response.Redirect(context.Failure?.Message == "oidc.not_admin" ? DeniedRedirect : SignInFailedRedirect);
                    context.HandleResponse(); return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    context.HttpContext.RequestAborted.ThrowIfCancellationRequested();
                    context.Response.Redirect(SignInFailedRedirect); context.HandleResponse(); return Task.CompletedTask;
                }
            };
        });
        services.AddOptions<OpenIdConnectOptions>(AdminOidcConstants.OidcScheme)
            .Configure<CompactStateDataFormat, TimeProvider>((options, state, time) =>
            {
                options.StateDataFormat = state;
                options.TimeProvider = time;
                options.TokenHandler = new StrictIdTokenHandler(settings, time);
            });
        services.PostConfigure<AuthenticationOptions>(options =>
            options.Schemes.Single(s => s.Name == AdminOidcConstants.OidcScheme).HandlerType = typeof(SafeOpenIdConnectHandler));
        services.AddTransient<SafeOpenIdConnectHandler>();
        return services;
    }

    /// <summary>
    /// Loads and pins the discovery document: the issuer must equal the configured authority
    /// and every used endpoint must be a safe absolute URI; PAR must stay optional.
    /// </summary>
    internal static async Task<OpenIdConnectConfiguration> Metadata(
        OpenIdConnectOptions options, AdminOidcSettings settings, CancellationToken cancellationToken)
    {
        var metadata = await options.ConfigurationManager!.GetConfigurationAsync(cancellationToken);
        if (metadata.Issuer != settings.Authority
            || !AdminOidcSettings.IsSafeUri(metadata.AuthorizationEndpoint, !options.RequireHttpsMetadata, out _)
            || !AdminOidcSettings.IsSafeUri(metadata.TokenEndpoint, !options.RequireHttpsMetadata, out _)
            || !AdminOidcSettings.IsSafeUri(metadata.JwksUri, !options.RequireHttpsMetadata, out _)
            || metadata.RequirePushedAuthorizationRequests)
            throw new InvalidOperationException("oidc.invalid_metadata");
        return metadata;
    }
}
