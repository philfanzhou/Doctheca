using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public sealed class QuestionBankServiceKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<InternalAuthOptions> internalAuthOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, logger, encoder)
{
    private readonly InternalAuthOptions _internalAuthOptions = internalAuthOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = _internalAuthOptions.QuestionBankKey;
        if (string.IsNullOrWhiteSpace(expected))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "QuestionBank service authentication is not configured."));
        }

        if (!Request.Headers.TryGetValue(
                DocLibraryAuthenticationConstants.QuestionBankHeaderName,
                out var values)
            || values.Count != 1
            || string.IsNullOrWhiteSpace(values[0]))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "QuestionBank service credential is required."));
        }

        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(values[0]!));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        if (!CryptographicOperations.FixedTimeEquals(providedHash, expectedHash))
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "QuestionBank service credential is invalid."));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "question-bank"),
            new Claim(ClaimTypes.Name, "QuestionBank")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, Scheme.Name)));
    }
}
