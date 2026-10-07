namespace Doctheca.Host.Authentication;

/// <summary>
/// Normalizes backchannel timeouts of the hosted-login client's named HTTP client (issue #70).
/// The package treats a cancellation of the request token as caller cancellation and every
/// other failure as an unreachable authority, but its discovery path lets a client-side timeout
/// surface as a cancellation and escape as an unhandled exception. This handler rewrites
/// timeout cancellations — those the request token did not request — into transport failures,
/// so a discovery or token-endpoint timeout keeps answering the fixed identity-unavailable
/// outcome instead of a 5xx, exactly like the replaced self-written slice.
/// </summary>
public sealed class AdminOidcBackchannelTimeoutHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The authority request timed out.", new TimeoutException(exception.Message, exception));
        }
    }
}
