namespace Doctheca.Host.Authentication;

/// <summary>
/// Fixed identifiers of the SignaCore hosted-login slice (issue #47).
/// </summary>
/// <remarks>
/// The opaque session cookie name is deliberately distinct from the legacy
/// <c>docthecaAccessToken</c> cookie so the JwtBearer cookie fallback and the server-side
/// session can coexist until the password flow is retired.
/// </remarks>
public static class AdminOidcConstants
{
    public const string SessionScheme = "AdminSession";
    public const string OidcScheme = "AdminOidc";
    public const string CallbackPath = "/admin/auth/oidc/callback";
    public const string SessionCookie = "docthecaAdminSession";
    public const string CsrfCookie = "docthecaAdminCsrf";
    public const string CsrfHeader = "X-CSRF-TOKEN";
}
