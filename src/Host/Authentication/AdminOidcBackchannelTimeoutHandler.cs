namespace Doctheca.Host.Authentication;

/// <summary>
/// Normalizes backchannel timeouts of the hosted-login client's named HTTP client (issue #70).
/// A client-side timeout arrives as a cancellation that the request token did not request; left
/// alone it can escape as an unhandled exception instead of a bounded sign-in failure. This
/// handler rewrites such timeouts into transport failures, so a discovery or token-endpoint
/// timeout keeps answering the fixed identity-unavailable outcome instead of a 5xx.
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
