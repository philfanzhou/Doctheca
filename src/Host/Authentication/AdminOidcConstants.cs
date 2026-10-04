namespace Doctheca.Host.Authentication;

/// <summary>
/// Fixed identifiers of the SignaCore hosted-login slice (issue #47).
/// </summary>
public static class AdminOidcConstants
{
    public const string SessionScheme = "AdminSession";
    public const string OidcScheme = "AdminOidc";
    public const string CallbackPath = "/admin/auth/oidc/callback";
    public const string SessionCookie = "docthecaAdminSession";
    public const string CsrfCookie = "docthecaAdminCsrf";
    public const string LogoutReturnPath = "/admin/auth/oidc/logout/return";
    public const string LogoutBindingCookie = "docthecaLogoutBinding";
    public const string CsrfHeader = "X-CSRF-TOKEN";
}
