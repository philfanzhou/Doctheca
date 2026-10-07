namespace Doctheca.Host.Authentication;

/// <summary>
/// Fixed public identifiers of the SignaCore hosted-login slice. The protocol itself lives in
/// the official <c>SignaCore.Client.AspNetCore</c> package (issue #70); these names stay stable
/// so the browser-facing contract (cookie and header names, route paths) is unchanged.
/// </summary>
public static class AdminOidcConstants
{
    /// <summary>The hosted-login route prefix the package endpoints are mounted under.</summary>
    public const string Prefix = "/admin/auth/oidc";
    public const string CallbackPath = "/admin/auth/oidc/callback";
    public const string SessionCookie = "docthecaAdminSession";
    public const string CsrfCookie = "docthecaAdminCsrf";
    public const string LogoutReturnPath = "/admin/auth/oidc/logout/return";
    public const string CsrfHeader = "X-CSRF-TOKEN";
    /// <summary>The SPA landing a completed prepared logout returns to (the package's
    /// PostLogoutReturnPath).</summary>
    public const string SignedOutRedirect = "/?authResult=signedOut";
}
