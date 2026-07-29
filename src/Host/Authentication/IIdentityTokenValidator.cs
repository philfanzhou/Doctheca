namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public interface IIdentityTokenValidator
{
    Task<ValidatedIdentityToken> ValidateTokenAsync(
        string accessToken,
        CancellationToken cancellationToken);
}
