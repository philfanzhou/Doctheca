using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ruoyu.Study.DocLibrary.Host.Authentication;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.Authentication;

public class IdentityAuthenticationServiceTests
{
    [Fact]
    public async Task PasswordGrantAsync_UsesCamelCaseContractAndConfiguredAppHeaders()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            captured = await CloneAsync(request);
            return JsonResponse("""
                {
                  "success": true,
                  "message": "ok",
                  "accessToken": "access-token",
                  "refreshToken": "refresh-token",
                  "expiresAt": 1999999999
                }
                """);
        });
        var service = CreateService(handler, appId: "doclibrary", appSecret: "secret");

        var result = await service.PasswordGrantAsync("admin", "password-value", CancellationToken.None);

        result.Status.Should().Be(IdentityExchangeStatus.Succeeded);
        captured.Should().NotBeNull();
        captured!.Headers.GetValues("X-Admin-AppId").Should().ContainSingle("doclibrary");
        captured.Headers.GetValues("X-Admin-AppSecret").Should().ContainSingle("secret");
        using var json = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        json.RootElement.GetProperty("grantType").GetString().Should().Be("password");
        json.RootElement.GetProperty("username").GetString().Should().Be("admin");
        json.RootElement.GetProperty("password").GetString().Should().Be("password-value");
        json.RootElement.TryGetProperty("grant_type", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_UsesRefreshTokenGrant()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
                {
                  "success": true,
                  "accessToken": "new-access",
                  "refreshToken": "new-refresh",
                  "expiresAt": 1999999999
                }
                """);
        });
        var service = CreateService(handler);

        var result = await service.RefreshAsync("old-refresh", CancellationToken.None);

        result.Status.Should().Be(IdentityExchangeStatus.Succeeded);
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("grantType").GetString().Should().Be("refresh_token");
        json.RootElement.GetProperty("refreshToken").GetString().Should().Be("old-refresh");
    }

    [Fact]
    public async Task PasswordGrantAsync_IdentityRejectsCredentials_ReturnsRejected()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse("""
            { "success": false, "message": "account not found" }
            """)));
        var service = CreateService(handler);

        var result = await service.PasswordGrantAsync("user", "bad", CancellationToken.None);

        result.Status.Should().Be(IdentityExchangeStatus.Rejected);
        result.Message.Should().BeNull();
    }

    [Fact]
    public async Task PasswordGrantAsync_MalformedSuccessResponse_ReturnsInvalidResponse()
    {
        var handler = new StubHttpMessageHandler(_ => Task.FromResult(JsonResponse("""
            { "success": true, "accessToken": "", "refreshToken": "" }
            """)));
        var service = CreateService(handler);

        var result = await service.PasswordGrantAsync("admin", "password", CancellationToken.None);

        result.Status.Should().Be(IdentityExchangeStatus.InvalidResponse);
    }

    [Fact]
    public async Task PasswordGrantAsync_TransportFailure_ReturnsUnavailable()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("offline"));
        var service = CreateService(handler);

        var result = await service.PasswordGrantAsync("admin", "password", CancellationToken.None);

        result.Status.Should().Be(IdentityExchangeStatus.Unavailable);
    }

    [Fact]
    public async Task RevokeAsync_SendsRefreshTokenWithoutReturningSecrets()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/auth/revoke");
            body = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{ "success": true }""");
        });
        var service = CreateService(handler);

        await service.RevokeAsync("refresh-to-revoke", CancellationToken.None);

        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("refreshToken").GetString().Should().Be("refresh-to-revoke");
    }

    [Theory]
    [InlineData("only-id", "")]
    [InlineData("", "only-secret")]
    public void Constructor_UnpairedAppCredentials_Throws(string appId, string appSecret)
    {
        var action = () => CreateService(new StubHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse("{}"))), appId, appSecret);

        action.Should().Throw<OptionsValidationException>();
    }

    private static IdentityAuthenticationService CreateService(
        HttpMessageHandler handler,
        string appId = "",
        string appSecret = "")
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://identity.test")
        };
        var options = Options.Create(new IdentityServiceOptions
        {
            Authority = "http://identity.test",
            AppId = appId,
            AppSecret = appSecret
        });
        return new IdentityAuthenticationService(
            client,
            options,
            NullLogger<IdentityAuthenticationService>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        if (request.Content != null)
        {
            clone.Content = new StringContent(
                await request.Content.ReadAsStringAsync(),
                Encoding.UTF8,
                "application/json");
        }
        return clone;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
