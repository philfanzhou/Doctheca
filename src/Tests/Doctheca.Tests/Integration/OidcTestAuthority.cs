using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace Doctheca.Tests.Integration;

// In-process HTTP authority emulating SignaCore's discovery, JWKS, authorize and token
// endpoints for the hosted-login tests: no production network, credentials, or signing key.
// Every string marked "canary" or "fictitious" exists to assert that secrets never reach a
// response, log, or trace surface.
internal sealed class OidcTestAuthority : HttpMessageHandler
{
    internal const string Issuer = "https://identity.example.test";
    internal const string ClientId = "oidc-test-app";
    internal const string Secret = "fictitious-oidc-secret-canary";
    internal const string PostLogoutUri = "https://admin.example.test/admin/auth/oidc/logout/return";
    internal const string RedirectUri = "https://admin.example.test/admin/auth/oidc/callback";
    internal string AuthorityIssuer { get; set; } = Issuer;
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, (string Nonce, string Challenge, string Defect, string AccessDefect)> _codes = [];
    internal readonly ConcurrentQueue<Dictionary<string, string>> LogoutForms = [];
    internal string? LogoutFailure { get; set; }
    internal string? LogoutUrl { get; set; }
    internal bool HoldLogout { get; set; }
    internal TaskCompletionSource LogoutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource LogoutRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ConcurrentQueue<Dictionary<string, string>> TokenForms = [];
    internal readonly ConcurrentQueue<string?> AuthorizationHeaders = [];
    internal string? DiscoveryDefect { get; set; }
    internal string? TokenFailure { get; set; }
    internal bool HoldToken { get; set; }
    internal bool HoldDiscovery { get; set; }
    internal TaskCompletionSource DiscoveryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource TokenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? LastIdToken { get; private set; }
    internal string? LastAccessToken { get; private set; }
    internal string? LastVerifier { get; private set; }
    internal int Redeems => TokenForms.Count;

    internal string Code(IDictionary<string, string> query, string defect = "valid", string accessDefect = "admin")
    {
        var code = "fictitious-code-" + Guid.NewGuid().ToString("N");
        _codes[code] = (query["nonce"], query["code_challenge"], defect, accessDefect);
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
        {
            DiscoveryEntered.TrySetResult();
            if (HoldDiscovery) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (DiscoveryDefect == "500") return new(HttpStatusCode.InternalServerError);
            if (DiscoveryDefect == "json") return Json("invalid-json");
            if (DiscoveryDefect == "timeout") throw new TaskCanceledException("fake.timeout");
            return Json(JsonSerializer.Serialize(new
            {
                issuer = DiscoveryDefect == "issuer" ? AuthorityIssuer + "/wrong" : AuthorityIssuer,
                authorization_endpoint = AuthorityIssuer + "/authorize", token_endpoint = AuthorityIssuer + "/token", jwks_uri = AuthorityIssuer + "/keys",
                response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" }, scopes_supported = new[] { "openid", "profile" },
                code_challenge_methods_supported = new[] { "S256" },
                pushed_authorization_request_endpoint = AuthorityIssuer + "/must-not-use-par"
            }));
        }
        if (request.RequestUri.AbsolutePath == "/keys")
        {
            if (DiscoveryDefect == "jwks") return Json("invalid-json");
            var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "test-kid" });
            return Json(JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid = "test-kid", use = "sig", alg = "RS256", n = key.N, e = key.E } } }));
        }
        if (request.RequestUri.AbsolutePath == "/oauth2/logout/requests")
        {
            var logoutForm = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(p => p.Key, p => p.Value.ToString());
            LogoutForms.Enqueue(logoutForm);
            LogoutEntered.TrySetResult();
            if (HoldLogout) await LogoutRelease.Task.WaitAsync(cancellationToken);
            if (LogoutFailure == "500") return new(HttpStatusCode.InternalServerError);
            if (LogoutFailure == "404") return new(HttpStatusCode.NotFound);
            if (LogoutFailure == "timeout") throw new TaskCanceledException("fictitious.logout_timeout");
            if (LogoutFailure == "json") return Json("invalid-json");
            if (LogoutFailure == "oversized") return Json(new string('x', 16385));
            if (LogoutFailure == "duplicate") return Json("{\"logout_uri\":\"/oauth2/logout?logout_handle=" + new string('h',43) + "\",\"logout_uri\":\"https://external.example\"}");
            return Json(JsonSerializer.Serialize(new { logout_uri = LogoutUrl ?? "/oauth2/logout?logout_handle=" + new string('h', 43) }));
        }
        if (request.RequestUri.AbsolutePath != "/token") throw new InvalidOperationException("fake.unexpected_endpoint");
        var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
        var form = QueryHelpers.ParseQuery(raw).ToDictionary(p => p.Key, p => p.Value.ToString());
        TokenForms.Enqueue(form);
        AuthorizationHeaders.Enqueue(request.Headers.Authorization?.ToString());
        TokenEntered.TrySetResult();
        if (HoldToken) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        if (TokenFailure == "500") return new(HttpStatusCode.InternalServerError);
        if (TokenFailure == "json") return Json("invalid-json");
        if (TokenFailure == "timeout") throw new TaskCanceledException("fake.timeout");
        if (!_codes.TryRemove(form["code"], out var handshake)) return new(HttpStatusCode.BadRequest);
        LastVerifier = form["code_verifier"];
        if (WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(LastVerifier))) != handshake.Challenge)
            throw new InvalidOperationException("fake.pkce_mismatch");
        LastIdToken = MintIdToken(handshake.Nonce, handshake.Defect);
        LastAccessToken = MintAccessToken(handshake.AccessDefect);
        return Json(JsonSerializer.Serialize(new { access_token = LastAccessToken, token_type = "Bearer", expires_in = 900,
            id_token = LastIdToken, scope = "openid profile" }));
    }

    internal SecurityKey SigningKey => new RsaSecurityKey(_rsa) { KeyId = "test-kid" };

    /// <summary>
    /// A Bearer token for the legacy JwtBearer path (shared platform audience), minted with the
    /// same key so the static in-memory configuration manager validates it.
    /// </summary>
    internal string LegacyBearer()
    {
        var header = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"at+jwt\",\"kid\":\"test-kid\"}"));
        var payload = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { iss = Issuer, aud = "PlatformAudience", sub = "fake-user", role = "admin", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() })));
        var input = header + "." + payload;
        return input + "." + WebEncoders.Base64UrlEncode(_rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    // The access token is a real signed JWT (typ at+jwt, aud = the application id) because the
    // host fully validates it and asserts role=admin before signing a session in.
    private string MintAccessToken(string defect)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = defect == "access-typ" ? "JWT" : "at+jwt", kid = "test-kid" });
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = defect == "access-issuer" ? "https://wrong.example.test" : AuthorityIssuer,
            ["aud"] = defect == "access-aud" ? "wrong-audience" : ClientId,
            ["sub"] = "fake-subject", ["iat"] = now, ["exp"] = now + 900,
            ["unique_name"] = "fake-name"
        };
        payload["role"] = defect == "user" ? "user" : "admin";
        if (defect == "user") payload["role"] = "user";
        if (defect == "none") payload.Remove("role");
        if (defect == "access-expired") { payload["iat"] = now - 900; payload["exp"] = now - 60; }
        if (defect == "sub-mismatch") payload["sub"] = "different-subject";
        if (defect == "sub-missing") payload.Remove("sub");
        if (defect == "sub-empty") payload["sub"] = "";
        if (defect == "sub-number") payload["sub"] = 123;
        if (defect == "sub-array") payload["sub"] = new[] { "fake-subject" };
        if (defect == "iss-array") payload["iss"] = new[] { Issuer };
        if (defect == "iss-number") payload["iss"] = 123;
        if (defect == "iss-empty") payload["iss"] = "";
        var raw = JsonSerializer.Serialize(payload);
        if (defect == "sub-duplicate") raw = raw.TrimEnd('}') + ",\"sub\":\"fake-subject\"}";
        if (defect == "iss-duplicate") raw = raw.TrimEnd('}') + ",\"iss\":\"" + AuthorityIssuer + "\"}";
        return Sign(header, raw, defect == "access-signature");
    }

    private string MintIdToken(string nonce, string defect)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = defect == "unsigned" ? "none" : defect == "alg" ? "HS256" : "RS256",
            typ = defect == "typ" ? "at+jwt" : "JWT", kid = defect == "kid" ? "unknown-kid" : "test-kid" });
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = defect == "issuer" ? "https://wrong.example.test" : AuthorityIssuer,
            ["aud"] = defect == "aud" ? "wrong-audience" : ClientId,
            ["sub"] = "fake-subject", ["iat"] = now, ["exp"] = now + 300,
            ["nonce"] = defect == "nonce" ? "wrong-nonce" : nonce,
            ["name"] = "fake-name", ["nickname"] = "fake-display", ["role"] = "admin"
        };
        if (defect == "aud-array") payload["aud"] = new[] { ClientId, "additional" };
        if (defect == "aud-single-array") payload["aud"] = new[] { ClientId };
        if (defect == "sub-missing") payload.Remove("sub");
        if (defect == "sub-empty") payload["sub"] = "";
        if (defect == "sub-array") payload["sub"] = new[] { "one", "two" };
        if (defect == "sub-number") payload["sub"] = 123;
        if (defect == "iss-array") payload["iss"] = new[] { Issuer };
        if (defect == "iss-number") payload["iss"] = 123;
        if (defect == "iss-empty") payload["iss"] = "";
        if (defect == "iat-missing") payload.Remove("iat");
        if (defect == "iat-future") payload["iat"] = now + 120;
        if (defect == "iat-string") payload["iat"] = now.ToString();
        if (defect == "exp-before-iat") payload["exp"] = now - 1;
        if (defect == "exp-expired") { payload["iat"] = now - 600; payload["exp"] = now - 120; }
        if (defect == "nonce-missing") payload.Remove("nonce");
        var raw = JsonSerializer.Serialize(payload);
        if (defect == "sub-duplicate") raw = raw.TrimEnd('}') + ",\"sub\":\"duplicate\"}";
        if (defect == "iss-duplicate") raw = raw.TrimEnd('}') + ",\"iss\":\"" + AuthorityIssuer + "\"}";
        return Sign(header, raw, defect == "signature", unsigned: defect == "unsigned");
    }

    private string Sign(string header, string payload, bool wrongKey, bool unsigned = false)
    {
        var input = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(header)) + "." + WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        if (unsigned) return input + ".";
        using var other = RSA.Create(2048);
        var signature = (wrongKey ? other : _rsa).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return input + "." + WebEncoders.Base64UrlEncode(signature);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
}
