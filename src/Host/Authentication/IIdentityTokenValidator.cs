namespace Doctheca.Host.Authentication;

public interface IIdentityTokenValidator
{
    Task<ValidatedIdentityToken> ValidateTokenAsync(
        string accessToken,
        CancellationToken cancellationToken);
}
