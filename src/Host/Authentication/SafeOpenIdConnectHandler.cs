using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Framework protocol diagnostics include Location, cookies, and untrusted response or
/// exception details at Debug/Error. This handler keeps them out of every configured logging
/// provider, even at Trace, and only ever handles the registered callback path.
/// </summary>
public sealed class SafeOpenIdConnectHandler(IOptionsMonitor<OpenIdConnectOptions> options,
    HtmlEncoder htmlEncoder, UrlEncoder encoder) : OpenIdConnectHandler(options, NullLoggerFactory.Instance, htmlEncoder, encoder)
{
    public override Task<bool> HandleRequestAsync() => Request.Path == AdminOidcConstants.CallbackPath
        ? base.HandleRequestAsync()
        : Task.FromResult(false);

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        try { Context.RequestAborted.ThrowIfCancellationRequested(); await base.HandleChallengeAsync(properties); }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested) { throw; }
        catch { Response.Redirect("/?authError=signInFailed"); }
    }
}
