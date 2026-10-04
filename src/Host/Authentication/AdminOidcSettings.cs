namespace Doctheca.Host.Authentication;

/// <summary>
/// Bound and validated configuration of the SignaCore hosted-login slice.
/// </summary>
/// <remarks>
/// <para>
/// <c>AdminOidc:Enabled</c> defaults to <c>false</c>: the new endpoints answer a fixed
/// 503 and every other route keeps its exact legacy behavior. When enabled, the application
/// credentials and trust anchor are reused from the existing <c>IdentityService</c> section;
/// the exact callback and post-logout URIs are new. Failure diagnostics deliberately contain nothing but
/// the offending configuration key.
/// </para>
/// <para>
/// ToString hides every bound value so the record can never leak a secret through a log.
/// </para>
/// </remarks>
public sealed record AdminOidcSettings(bool Enabled, string Authority, string ClientId, string ClientSecret,
    string RedirectUri, bool InsecureLoopback, TimeSpan ClockSkew, string PostLogoutRedirectUri = "")
{
    internal const string DisabledError = "Hosted sign-in is not enabled.";

    internal static AdminOidcSettings Read(IConfiguration config, IHostEnvironment environment)
    {
        if (!config.GetValue<bool>("AdminOidc:Enabled"))
        {
            return new(false, "", "", "", "", false, TimeSpan.Zero);
        }

        var dev = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var redirect = config["AdminOidc:RedirectUri"] ?? "";
        var authority = (config["IdentityService:Authority"] ?? "").TrimEnd('/');
        if (!IsSafeUri(redirect, dev, out var redirectUri) || redirectUri!.AbsolutePath != "/admin/auth/oidc/callback"
            || redirectUri.AbsoluteUri != redirect || redirect.Length > 500 || redirect.Any(c => c > 127))
            throw new InvalidOperationException("AdminOidc:RedirectUri");
        if (!IsSafeUri(authority, dev, out _)) throw new InvalidOperationException("IdentityService:Authority");
        var postLogout = config["AdminOidc:PostLogoutRedirectUri"] ?? "";
        if (!IsSafeUri(postLogout, dev, out var postLogoutUri)
            || postLogoutUri!.AbsolutePath != AdminOidcConstants.LogoutReturnPath
            || postLogoutUri.AbsoluteUri != postLogout || postLogout.Length > 500 || postLogout.Any(c => c > 127)
            || postLogoutUri.GetLeftPart(UriPartial.Authority) != redirectUri.GetLeftPart(UriPartial.Authority))
            throw new InvalidOperationException("AdminOidc:PostLogoutRedirectUri");
        var clientId = config["IdentityService:AppId"];
        var secret = config["IdentityService:AppSecret"];
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("IdentityService:AppId");
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("IdentityService:AppSecret");
        var skew = config.GetValue<int?>("IdentityService:ClockSkewSeconds") ?? 30;
        if (skew is < 0 or > 300) throw new InvalidOperationException("IdentityService:ClockSkewSeconds");
        return new(true, authority, clientId, secret, redirect, redirectUri.Scheme == "http", TimeSpan.FromSeconds(skew), postLogout);
    }

    internal static bool IsSafeUri(string value, bool allowLoopback, out Uri? uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri)
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && !value.Any(char.IsControl) && !value.Contains('*')
            && (uri.Scheme == "https" || allowLoopback && uri.Scheme == "http" && uri.Host is "127.0.0.1" or "[::1]");
    }

    /// <summary>
    /// Validates the <c>returnUrl</c> of the sign-in entry: only same-site absolute paths are
    /// accepted. Encoded alternate origins, backslashes, control characters, dot segments, and
    /// the auth area itself (which would loop the flow) are rejected in their raw and decoded
    /// forms; the decoded form must stay a same-site absolute path.
    /// </summary>
    internal static bool IsValidReturnUrl(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2048) return false;
        var candidate = path;
        for (var i = 0; i < 4; i++)
        {
            if (!candidate.StartsWith('/') || candidate.StartsWith("//") || candidate.Contains('\\')
                || candidate.Any(char.IsControl)) return false;
            var route = candidate.Split('?', '#')[0];
            if (route.Split('/').Any(segment => segment is "." or "..")) return false;
            if (route.Equals("/admin/auth", StringComparison.OrdinalIgnoreCase)
                || route.StartsWith("/admin/auth/", StringComparison.OrdinalIgnoreCase)) return false;
            var decoded = Uri.UnescapeDataString(candidate);
            if (decoded == candidate) return true;
            candidate = decoded;
        }
        return false;
    }

    public override string ToString() => "AdminOidcSettings";
}
