using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Doctheca.Host.Authentication;

public sealed class IdentityAuthenticationService : IIdentityAuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly IdentityClientCredentialsOptions _credentials;
    private readonly ILogger<IdentityAuthenticationService> _logger;

    public IdentityAuthenticationService(
        HttpClient httpClient,
        IOptions<IdentityClientCredentialsOptions> credentials,
        ILogger<IdentityAuthenticationService> logger)
    {
        _httpClient = httpClient;
        _credentials = credentials.Value;
        _logger = logger;
    }

    public Task<IdentityTokenExchangeResult> PasswordGrantAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        return ExchangeAsync(
            new IdentityTokenRequest("password", username, password),
            cancellationToken);
    }

    public Task<IdentityTokenExchangeResult> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken)
    {
        return ExchangeAsync(
            new IdentityTokenRequest("refresh_token", RefreshToken: refreshToken),
            cancellationToken);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Post,
            "/api/auth/revoke",
            JsonContent.Create(new IdentityRevokeRequest(refreshToken)));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<IdentityTokenExchangeResult> ExchangeAsync(
        IdentityTokenRequest tokenRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateRequest(
                HttpMethod.Post,
                "/api/auth/token",
                JsonContent.Create(tokenRequest));
            request.Headers.Add("X-Admin-AppId", _credentials.AppId);
            request.Headers.Add("X-Admin-AppSecret", _credentials.AppSecret);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Identity rejected the Doctheca application credentials");
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.Unavailable);
            }

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.Rejected);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Identity token request failed with status code {StatusCode}",
                    (int)response.StatusCode);
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.Unavailable);
            }

            var content = await response.Content.ReadFromJsonAsync<IdentityTokenResponse>(
                cancellationToken: cancellationToken);
            if (content == null)
            {
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.InvalidResponse);
            }

            if (!content.Success)
            {
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.Rejected);
            }

            if (string.IsNullOrWhiteSpace(content.AccessToken)
                || string.IsNullOrWhiteSpace(content.RefreshToken)
                || content.ExpiresAt <= 0)
            {
                return new IdentityTokenExchangeResult(IdentityExchangeStatus.InvalidResponse);
            }

            return new IdentityTokenExchangeResult(
                IdentityExchangeStatus.Succeeded,
                content.AccessToken,
                content.RefreshToken,
                content.ExpiresAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Identity token request timed out");
            return new IdentityTokenExchangeResult(IdentityExchangeStatus.Unavailable);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Identity token request failed: {ExceptionType}", ex.GetType().Name);
            return new IdentityTokenExchangeResult(IdentityExchangeStatus.Unavailable);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Identity token response contained invalid JSON");
            return new IdentityTokenExchangeResult(IdentityExchangeStatus.InvalidResponse);
        }
        catch (NotSupportedException)
        {
            _logger.LogWarning("Identity token response used an unsupported content type");
            return new IdentityTokenExchangeResult(IdentityExchangeStatus.InvalidResponse);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent content)
    {
        return new HttpRequestMessage(method, path)
        {
            Content = content
        };
    }
}
