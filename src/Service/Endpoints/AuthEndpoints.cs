using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.DocLibrary.Service;

/// <summary>
/// ILogger category marker for AuthEndpoints.
/// </summary>
internal class AuthEndpointsLogger { }

/// <summary>
/// 认证端点，代理 Identity HTTP API POST /api/auth/token。
/// Identity 去 gRPC Phase 1：替代原 Identity.Client SDK 的 MapIdentityAuthEndpoints。
/// 端点路径与原 SDK 保持一致（/admin/auth/login、/refresh、/me、/logout），前端无需修改。
/// </summary>
public static class AuthEndpoints
{
    private const string AppIdHeader = "X-Admin-AppId";
    private const string AppSecretHeader = "X-Admin-AppSecret";

    public static WebApplication MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/admin/auth");

        group.MapPost("/login", Login).AllowAnonymous();
        group.MapPost("/refresh", Refresh).AllowAnonymous();
        group.MapGet("/me", GetCurrentUser).RequireAuthorization();
        group.MapPost("/logout", Logout).RequireAuthorization();

        return app;
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AuthEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Results.BadRequest(new { success = false, message = "用户名和密码不能为空" });

        try
        {
            using var httpClient = httpClientFactory.CreateClient("IdentityService");
            AddGatewayHeaders(httpClient, configuration);

            var tokenRequest = new IdentityTokenRequest
            {
                GrantType = "password",
                Username = request.Username.Trim(),
                Password = request.Password
            };

            using var httpResponse = await httpClient.PostAsJsonAsync("/api/auth/token", tokenRequest, cancellationToken);
            if (!httpResponse.IsSuccessStatusCode)
            {
                logger.LogError("Identity token request failed: StatusCode={StatusCode}, Username={Username}",
                    httpResponse.StatusCode, request.Username.Trim());
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            var response = await httpResponse.Content.ReadFromJsonAsync<IdentityTokenResponse>(cancellationToken: cancellationToken);
            if (response == null)
            {
                logger.LogError("Identity token response is empty, Username={Username}", request.Username.Trim());
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            if (!response.Success)
            {
                logger.LogWarning("Login failed: Username={Username}, Reason={Reason}",
                    request.Username.Trim(), response.Message);
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    accessToken = response.AccessToken,
                    refreshToken = response.RefreshToken,
                    expiresIn = response.ExpiresIn,
                    expiresAt = response.ExpiresAt,
                    userInfo = new
                    {
                        userId = response.UserInfo?.UserId ?? string.Empty,
                        username = response.UserInfo?.Username ?? string.Empty,
                        authMethod = response.UserInfo?.AuthMethod ?? string.Empty,
                        roles = response.UserInfo?.Roles ?? new List<string>(),
                        permissions = response.UserInfo?.Permissions ?? new List<string>()
                    }
                }
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Identity HTTP call failed: Username={Username}", request.Username.Trim());
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AuthEndpointsLogger> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Results.BadRequest(new { success = false, message = "RefreshToken 不能为空" });

        try
        {
            using var httpClient = httpClientFactory.CreateClient("IdentityService");
            AddGatewayHeaders(httpClient, configuration);

            var tokenRequest = new IdentityTokenRequest
            {
                GrantType = "refresh_token",
                RefreshToken = request.RefreshToken
            };

            using var httpResponse = await httpClient.PostAsJsonAsync("/api/auth/token", tokenRequest, cancellationToken);
            if (!httpResponse.IsSuccessStatusCode)
            {
                logger.LogError("Identity refresh request failed: StatusCode={StatusCode}", httpResponse.StatusCode);
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            var response = await httpResponse.Content.ReadFromJsonAsync<IdentityTokenResponse>(cancellationToken: cancellationToken);
            if (response == null)
            {
                logger.LogError("Identity refresh response is empty");
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            }

            if (!response.Success)
            {
                logger.LogWarning("Token refresh failed: Reason={Reason}", response.Message);
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                success = true,
                data = new
                {
                    accessToken = response.AccessToken,
                    refreshToken = response.RefreshToken,
                    expiresIn = response.ExpiresIn,
                    expiresAt = response.ExpiresAt
                }
            });
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Identity HTTP call failed (refresh)");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    private static Task<IResult> GetCurrentUser(ClaimsPrincipal user)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;
        var username = user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
        var authMethod = user.FindFirstValue("auth_method") ?? string.Empty;
        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        var permissions = user.FindAll("Permission").Select(c => c.Value).ToList();

        return Task.FromResult(Results.Ok(new
        {
            success = true,
            data = new { userId, username, authMethod, roles, permissions }
        }));
    }

    private static Task<IResult> Logout()
    {
        // JWT 是无状态的，登出主要是前端清除本地 Token。
        // DocLibrary 不再调用 Identity 吊销 RefreshToken（与原 SDK 行为一致）。
        return Task.FromResult(Results.Ok(new { success = true, message = "已登出" }));
    }

    private static void AddGatewayHeaders(HttpClient httpClient, IConfiguration configuration)
    {
        var appId = configuration["IdentityService:AppId"];
        var appSecret = configuration["IdentityService:AppSecret"];
        if (!string.IsNullOrEmpty(appId))
            httpClient.DefaultRequestHeaders.Add(AppIdHeader, appId);
        if (!string.IsNullOrEmpty(appSecret))
            httpClient.DefaultRequestHeaders.Add(AppSecretHeader, appSecret);
    }
}

/// <summary>
/// 登录请求。
/// </summary>
public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Token 刷新请求。
/// </summary>
public sealed class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Identity HTTP API POST /api/auth/token 请求体（OAuth2 grant_type 模式）。
/// 仅 AuthEndpoints 内部使用，字段对应 Identity TokenRequest。
/// </summary>
internal sealed class IdentityTokenRequest
{
    public string GrantType { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Phone { get; set; }
    public string? Code { get; set; }
    public string? RefreshToken { get; set; }
}

/// <summary>
/// Identity HTTP API POST /api/auth/token 响应体。
/// 仅 AuthEndpoints 内部使用，字段对应 Identity TokenResponse（System.Text.Json 默认 camelCase 反序列化）。
/// </summary>
internal sealed class IdentityTokenResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public long ExpiresIn { get; set; }
    public long ExpiresAt { get; set; }
    public IdentityUserInfo? UserInfo { get; set; }
}

internal sealed class IdentityUserInfo
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string AuthMethod { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = new();
    public List<string> Permissions { get; set; } = new();
}
