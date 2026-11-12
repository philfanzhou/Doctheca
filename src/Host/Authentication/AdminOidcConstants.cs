namespace Doctheca.Host.Authentication;

/// <summary>
/// Fixed public identifiers of the admin hosted-login slice. The protocol runs on the framework's
/// own OpenID Connect handler (<c>Microsoft.AspNetCore.Authentication.OpenIdConnect</c>, part of
/// the shared framework — no client package); these names stay stable so the browser-facing
/// contract (cookie and header names, route paths) is unchanged from the replaced
/// <c>SignaCore.Client.AspNetCore</c> integration.
/// </summary>
public static class AdminOidcConstants
{
    /// <summary>The hosted-login route prefix the slice's own endpoints are mounted under.</summary>
    public const string Prefix = "/admin/auth/oidc";
    public const string CallbackPath = "/admin/auth/oidc/callback";
    public const string SessionCookie = "docthecaAdminSession";
    public const string CsrfCookie = "docthecaAdminCsrf";
    public const string LogoutReturnPath = "/admin/auth/oidc/logout/return";
    public const string CsrfHeader = "X-CSRF-TOKEN";
    /// <summary>The SPA landing a completed sign-out returns to (the protected <c>state</c>
    /// round-trip of the standard RP-initiated logout ends here).</summary>
    public const string SignedOutRedirect = "/?authResult=signedOut";

    /// <summary>
    /// The cookie scheme holding the server-side session: its opaque, data-protected cookie
    /// references a ticket in the in-process <see cref="AdminOidcTicketStore"/>. Named after the
    /// public cookie it owns.
    /// </summary>
    public const string SessionScheme = "docthecaAdminSession";

    /// <summary>The OpenID Connect scheme driving the authorization-code flow and the standard
    /// RP-initiated logout against the configured authority.</summary>
    public const string OidcScheme = "oidc";

    /// <summary>
    /// The forwarding scheme the admin authorization policy authenticates against: it routes each
    /// request to the host's JwtBearer handler exactly when the session boundary has independently
    /// verified the Bearer credential (see <see cref="AdminOidcSessionMiddleware"/>), and to the
    /// cookie session everywhere else. Replaces the package's <c>SignaCoreHostedLogin</c> scheme.
    /// </summary>
    public const string AuthenticationScheme = "docthecaAdmin";

    /// <summary>The named <c>HttpClient</c> wired as the OpenID Connect backchannel: bounded to 30
    /// seconds, redirect-free, cookie-free, unlogged, and carrying the timeout normalization.</summary>
    public const string HttpClientName = "DocthecaAdminOidc";

    /// <summary>
    /// The failure marker of the admin pre-sign-in gate: a subject without the <c>admin</c> role
    /// fails token validation before any session is written, and the failure surfaces to the
    /// browser as the fixed <c>notAdmin</c> landing.
    /// </summary>
    internal const string NotAdminFailure = "The signed-in subject is not an administrator.";

    /// <summary>
    /// The failure marker of an authorization response that is not a usable, single-authority
    /// response: duplicated protocol parameters, a missing RFC 9207 issuer parameter, or an
    /// issuer that does not match the discovery document.
    /// </summary>
    internal const string InvalidResponseFailure = "The authorization response is invalid.";
}
