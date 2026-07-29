using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Ruoyu.Study.DocLibrary.Host.Authentication;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.Authentication;

public class AdminAuthEndpointsTests
{
    [Fact]
    public async Task Login_ValidAdmin_SetsHttpOnlyCookiesWithoutReturningTokens()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.PasswordGrantAsync("admin", "correct", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokenExchangeResult(
                IdentityExchangeStatus.Succeeded,
                "access-secret",
                "refresh-secret",
                1999999999));
        var validator = CreateValidator(isAdministrator: true);
        await using var app = await CreateAppAsync(identity.Object, validator.Object);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/admin/auth/login",
            new { username = " admin ", password = "correct" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        cookies.Should().Contain(cookie =>
            cookie.StartsWith("doclibraryAccessToken=access-secret", StringComparison.Ordinal)
            && cookie.Contains("path=/admin", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(cookie =>
            cookie.StartsWith("doclibraryRefreshToken=refresh-secret", StringComparison.Ordinal)
            && cookie.Contains("path=/admin/auth", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("access-secret");
        body.Should().NotContain("refresh-secret");
        body.Should().Contain("\"username\":\"admin\"");
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("admin", "")]
    public async Task Login_MissingCredentials_Returns400(string username, string password)
    {
        await using var app = await CreateAppAsync(
            Mock.Of<IIdentityAuthenticationService>(),
            Mock.Of<IIdentityTokenValidator>());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/admin/auth/login",
            new { username, password });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Username and password are required.");
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401WithoutCookies()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.PasswordGrantAsync("admin", "wrong", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokenExchangeResult(IdentityExchangeStatus.Rejected));
        await using var app = await CreateAppAsync(identity.Object, Mock.Of<IIdentityTokenValidator>());

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/admin/auth/login",
            new { username = "admin", password = "wrong" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid username or password.");
    }

    [Fact]
    public async Task Login_ValidNormalUser_Returns403WithoutCookies()
    {
        var identity = SuccessfulIdentity();
        var validator = CreateValidator(isAdministrator: false);
        await using var app = await CreateAppAsync(identity.Object, validator.Object);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/admin/auth/login",
            new { username = "user", password = "correct" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await response.Content.ReadAsStringAsync()).Should().Contain("Administrator access is required.");
    }

    [Fact]
    public async Task Login_InvalidToken_Returns502WithoutCookies()
    {
        var identity = SuccessfulIdentity();
        var validator = new Mock<IIdentityTokenValidator>();
        validator.Setup(x => x.ValidateTokenAsync("access-secret", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SecurityTokenInvalidSignatureException("invalid"));
        await using var app = await CreateAppAsync(identity.Object, validator.Object);

        var response = await app.GetTestClient().PostAsJsonAsync(
            "/admin/auth/login",
            new { username = "admin", password = "correct" });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        response.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("Identity service returned an invalid token.");
    }

    [Fact]
    public async Task Refresh_ValidAdmin_RotatesBothCookies()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.RefreshAsync("old-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokenExchangeResult(
                IdentityExchangeStatus.Succeeded,
                "new-access",
                "new-refresh",
                1999999999));
        var validator = CreateValidator(isAdministrator: true, accessToken: "new-access");
        await using var app = await CreateAppAsync(identity.Object, validator.Object);
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/refresh");
        request.Headers.Add("Cookie", "doclibraryRefreshToken=old-refresh");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        cookies.Should().Contain(cookie => cookie.StartsWith(
            "doclibraryAccessToken=new-access", StringComparison.Ordinal));
        cookies.Should().Contain(cookie => cookie.StartsWith(
            "doclibraryRefreshToken=new-refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refresh_InvalidToken_Returns401AndClearsCookies()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.RefreshAsync("bad-refresh", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokenExchangeResult(IdentityExchangeStatus.Rejected));
        await using var app = await CreateAppAsync(identity.Object, Mock.Of<IIdentityTokenValidator>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/refresh");
        request.Headers.Add("Cookie", "doclibraryRefreshToken=bad-refresh");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        cookies.Should().Contain(cookie => cookie.StartsWith(
            "doclibraryAccessToken=", StringComparison.Ordinal));
        cookies.Should().Contain(cookie => cookie.StartsWith(
            "doclibraryRefreshToken=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Logout_RevokeFails_StillClearsCookiesAndReturns200()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.RevokeAsync("refresh", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));
        await using var app = await CreateAppAsync(identity.Object, Mock.Of<IIdentityTokenValidator>());
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/logout");
        request.Headers.Add("Cookie", "doclibraryRefreshToken=refresh");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").Should().HaveCount(2);
    }

    [Fact]
    public async Task Session_AuthenticatedAdmin_ReturnsSessionWithoutTokens()
    {
        await using var app = await CreateAppAsync(
            Mock.Of<IIdentityAuthenticationService>(),
            Mock.Of<IIdentityTokenValidator>());
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/auth/session");
        request.Headers.Add("X-Test-Admin", "true");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("11111111-1111-1111-1111-111111111111");
        body.Should().Contain("\"username\":\"admin\"");
        body.ToLowerInvariant().Should().NotContain("token");
    }

    private static Mock<IIdentityAuthenticationService> SuccessfulIdentity()
    {
        var identity = new Mock<IIdentityAuthenticationService>();
        identity.Setup(x => x.PasswordGrantAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokenExchangeResult(
                IdentityExchangeStatus.Succeeded,
                "access-secret",
                "refresh-secret",
                1999999999));
        return identity;
    }

    private static Mock<IIdentityTokenValidator> CreateValidator(
        bool isAdministrator,
        string accessToken = "access-secret")
    {
        var claims = new[]
        {
            new Claim("sub", "11111111-1111-1111-1111-111111111111"),
            new Claim("unique_name", "admin"),
            new Claim("role", isAdministrator ? "admin" : "user"),
            new Claim("exp", "1999999999")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        var validator = new Mock<IIdentityTokenValidator>();
        validator.Setup(x => x.ValidateTokenAsync(accessToken, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ValidatedIdentityToken(
                principal,
                "11111111-1111-1111-1111-111111111111",
                "admin",
                [isAdministrator ? "admin" : "user"],
                1999999999,
                isAdministrator));
        return validator;
    }

    private static async Task<WebApplication> CreateAppAsync(
        IIdentityAuthenticationService identity,
        IIdentityTokenValidator validator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(validator);
        builder.Services.Configure<DocLibraryCookieOptions>(options => options.CookieSecure = false);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderAdminAuthenticationHandler>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("DocLibraryAdmin", policy =>
            {
                policy.AddAuthenticationSchemes("Test");
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("role", "admin");
            });
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAdminAuthEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class HeaderAdminAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Request.Headers["X-Test-Admin"] != "true")
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim("sub", "11111111-1111-1111-1111-111111111111"),
                new Claim("unique_name", "admin"),
                new Claim("role", "admin"),
                new Claim("exp", "1999999999")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
