namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public interface IIdentityAuthenticationService
{
    Task<IdentityTokenExchangeResult> PasswordGrantAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    Task<IdentityTokenExchangeResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken);

    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken);
}
