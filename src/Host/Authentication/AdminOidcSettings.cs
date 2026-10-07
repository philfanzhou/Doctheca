namespace Doctheca.Host.Authentication;

/// <summary>
/// Bound and validated configuration of the SignaCore hosted-login slice. The availability
/// precheck runs before the official client package is registered: without the required keys the
/// package is never wired and every hosted-login endpoint answers the fixed 503 result.
/// </summary>
public sealed record AdminOidcSettings(bool Available, string Authority, string ClientId, string ClientSecret,
    string RedirectUri, bool InsecureLoopback = false, string PostLogoutRedirectUri = "")
{
    internal const string DisabledError = "Hosted sign-in is not configured.";
    public IReadOnlyList<string> MissingKeys { get; init; } = [];

    internal static AdminOidcSettings Read(IConfiguration config, IHostEnvironment environment)
    {
        var required = new[] { "IdentityService:Authority", "IdentityService:AppId", "IdentityService:AppSecret",
            "AdminOidc:RedirectUri", "AdminOidc:PostLogoutRedirectUri" };
        var missing = required.Where(key => string.IsNullOrWhiteSpace(config[key])).ToArray();
        if (missing.Length > 0)
            return new(false, "", "", "", "", false) { MissingKeys = missing };

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
        // The key stays part of the startup contract: an unparseable value still fails startup,
        // even though the package applies its own fixed 30-second token lifetime skew.
        var rawSkew = config["IdentityService:ClockSkewSeconds"];
        var skew = 30;
        if (rawSkew is not null && !int.TryParse(rawSkew, out skew))
            throw new InvalidOperationException("IdentityService:ClockSkewSeconds");
        if (skew is < 0 or > 300) throw new InvalidOperationException("IdentityService:ClockSkewSeconds");
        return new(true, authority, clientId, secret, redirect, redirectUri.Scheme == "http", postLogout);
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
    /// forms; the decoded form must stay a same-site absolute path. The official package accepts
    /// a weaker shape by itself, so the start guard keeps applying this exact rule.
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
