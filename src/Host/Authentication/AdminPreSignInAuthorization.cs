using System.Security.Claims;
using SignaCore.Client.AspNetCore;

namespace Doctheca.Host.Authentication;

/// <summary>
/// The pre-sign-in authorization gate of the hosted-login slice (issue #70): a new session may
/// only be established for a subject whose strictly validated SignaCore access token carries the
/// <c>admin</c> role. The package invokes the decision after both tokens are verified and
/// issuer/subject-correlated, and before any ticket or cookie is written; every non-Allowed
/// outcome (denial, exception, timeout) fails closed without touching existing sessions.
/// </summary>
/// <remarks>
/// The decision is a pure claim assertion on the verified access-token principal: no side
/// effects, no I/O, and it honours the cancellation token so timeouts cancel promptly.
/// </remarks>
public sealed class AdminPreSignInAuthorizationDecision : ISignaCorePreSignInAuthorizationDecision
{
    public static readonly AdminPreSignInAuthorizationDecision Instance = new();

    public ValueTask<SignaCoreAuthorizationDecisionResult> DecideAsync(
        SignaCorePreSignInAuthorizationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = context.AccessTokenPrincipal.Claims.Any(claim =>
            claim.Type is "role" or ClaimTypes.Role
            && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase));
        return ValueTask.FromResult(allowed
            ? SignaCoreAuthorizationDecisionResult.Allowed
            : SignaCoreAuthorizationDecisionResult.Denied);
    }
}
