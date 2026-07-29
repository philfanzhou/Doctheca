using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ruoyu.Study.DocLibrary.Host.Authentication;

public sealed class IdentityAuthenticationService : IIdentityAuthenticationService
{
    private readonly HttpClient _httpClient;
    private readonly IdentityServiceOptions _options;
    private readonly ILogger<IdentityAuthenticationService> _logger;

    public IdentityAuthenticationService(
        HttpClient httpClient,
        IOptions<IdentityServiceOptions> options,
        ILogger<IdentityAuthenticationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_options.AppId) != string.IsNullOrWhiteSpace(_options.AppSecret))
        {
            throw new OptionsValidationException(
                IdentityServiceOptions.SectionName,
                typeof(IdentityServiceOptions),
                ["IdentityService AppId and AppSecret must be configured together."]);
        }
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
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
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

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, HttpContent content)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = content
        };

        if (!string.IsNullOrWhiteSpace(_options.AppId))
        {
            request.Headers.TryAddWithoutValidation("X-Admin-AppId", _options.AppId);
            request.Headers.TryAddWithoutValidation("X-Admin-AppSecret", _options.AppSecret);
        }

        return request;
    }
}
