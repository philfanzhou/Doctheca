using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using QuantumZhou.Identity.Contract.Protos;

namespace Ruoyu.Study.DocRetrieval.Service;

/// <summary>
/// Marker class for ILogger category in AuthEndpoints.
/// </summary>
internal class AuthEndpointsLogger { }

public static class AuthEndpoints
{
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
        AuthGrpcService.AuthGrpcServiceClient identityClient,
        IConfiguration configuration,
        ILogger<AuthEndpointsLogger> logger)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return Results.BadRequest(new { success = false, message = "用户名和密码不能为空" });

        var appId = configuration["Identity:AppId"] ?? string.Empty;
        var appSecret = Environment.GetEnvironmentVariable("IDENTITY_APP_SECRET")
            ?? configuration["Identity:AppSecret"] ?? string.Empty;

        try
        {
            var tokenRequest = new GetTokenRequest
            {
                GrantType = "password",
                AppId = appId,
                AppSecret = appSecret,
                Password = new PasswordCredential
                {
                    Username = request.Username.Trim(),
                    Password = request.Password
                }
            };

            var response = await identityClient.GetTokenAsync(tokenRequest);

            if (!response.Success)
            {
                logger.LogWarning("登录失败: Username={Username}, Reason={Reason}",
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
                        userId = response.UserInfo.UserId,
                        username = response.UserInfo.Username,
                        authMethod = response.UserInfo.AuthMethod,
                        roles = response.UserInfo.Roles,
                        permissions = response.UserInfo.Permissions
                    }
                }
            });
        }
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogError(ex, "Identity gRPC 调用失败: Username={Username}", request.Username.Trim());
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> Refresh(
        RefreshRequest request,
        AuthGrpcService.AuthGrpcServiceClient identityClient,
        IConfiguration configuration,
        ILogger<AuthEndpointsLogger> logger)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            return Results.BadRequest(new { success = false, message = "RefreshToken 不能为空" });

        var appId = configuration["Identity:AppId"] ?? string.Empty;
        var appSecret = Environment.GetEnvironmentVariable("IDENTITY_APP_SECRET")
            ?? configuration["Identity:AppSecret"] ?? string.Empty;

        try
        {
            var tokenRequest = new GetTokenRequest
            {
                GrantType = "refresh_token",
                AppId = appId,
                AppSecret = appSecret,
                RefreshToken = new RefreshTokenCredential
                {
                    RefreshToken = request.RefreshToken
                }
            };

            var response = await identityClient.GetTokenAsync(tokenRequest);

            if (!response.Success)
            {
                logger.LogWarning("Token 刷新失败: Reason={Reason}", response.Message);
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
        catch (Grpc.Core.RpcException ex)
        {
            logger.LogError(ex, "Identity gRPC 调用失败 (refresh)");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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

    private static async Task<IResult> Logout(
        ClaimsPrincipal user,
        AuthGrpcService.AuthGrpcServiceClient identityClient,
        ILogger<AuthEndpointsLogger> logger)
    {
        // JWT 是无状态的，登出主要是前端清除本地 Token
        // 可选：吊销 RefreshToken（如果前端传了的话）
        return Results.Ok(new { success = true, message = "已登出" });
    }
}

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);
