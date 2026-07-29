using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Repositories;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Host.Authentication;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.Authentication;

public class AuthorizationBoundaryTests
{
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("doclibrary-test-signing-key-32bytes!!"));

    [Fact]
    public async Task AdminApi_Anonymous_Returns401()
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient().GetAsync("/admin/document-parses/");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AdminApi_AuthenticatedWithoutAdminRole_Returns403()
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/document-parses/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("user"));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminApi_AdminBearer_ReachesRealEndpoint()
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/document-parses/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("admin"));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminApi_AdminAccessCookie_ReachesRealEndpoint()
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/admin/document-parses/");
        request.Headers.Add(
            "Cookie",
            $"{DocLibraryAuthenticationConstants.AccessCookieName}={CreateToken("AdMiN")}");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task InternalPolicy_MissingOrWrongServiceKey_Returns401(string? serviceKey)
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/question-bank/test");
        if (serviceKey != null)
        {
            request.Headers.Add(DocLibraryAuthenticationConstants.QuestionBankHeaderName, serviceKey);
        }

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InternalPolicy_CorrectServiceKey_ReachesEndpoint()
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/question-bank/test");
        request.Headers.Add(DocLibraryAuthenticationConstants.QuestionBankHeaderName, "question-bank-key");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InternalPolicy_AdminCookieOnly_Returns401()
    {
        await using var app = await CreateAppAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/internal/question-bank/test");
        request.Headers.Add(
            "Cookie",
            $"{DocLibraryAuthenticationConstants.AccessCookieName}={CreateToken("admin")}");

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/")]
    [InlineData("/unmatched-spa-route")]
    public async Task HealthAndSpaFallback_AreAnonymous(string path)
    {
        await using var app = await CreateAppAsync();

        var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalAuth:QuestionBankKey"] = "question-bank-key"
        });

        var parseService = new Mock<IDocumentParseService>();
        parseService.Setup(x => x.GetListAsync(
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<string>()))
            .ReturnsAsync((new List<DocumentParseModel>(), 0));
        builder.Services.AddSingleton(parseService.Object);
        builder.Services.AddSingleton(Mock.Of<IDocumentFileService>());
        builder.Services.AddSingleton(Mock.Of<IOssService>());
        builder.Services.AddSingleton(Mock.Of<ISearchIndexService>());

        builder.Services.Configure<InternalAuthOptions>(
            builder.Configuration.GetSection(InternalAuthOptions.SectionName));
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = SigningKey,
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                    RoleClaimType = "role"
                };
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (string.IsNullOrWhiteSpace(context.Token))
                        {
                            context.Token = context.Request.Cookies[
                                DocLibraryAuthenticationConstants.AccessCookieName];
                        }
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, QuestionBankServiceKeyAuthenticationHandler>(
                DocLibraryAuthenticationConstants.QuestionBankScheme,
                _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(DocLibraryAuthorizationPolicies.Admin, policy =>
            {
                policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context => context.User.Claims.Any(claim =>
                    claim.Type == "role"
                    && string.Equals(claim.Value, "admin", StringComparison.OrdinalIgnoreCase)));
            });
            options.AddPolicy(DocLibraryAuthorizationPolicies.QuestionBank, policy =>
            {
                policy.AddAuthenticationSchemes(
                    DocLibraryAuthenticationConstants.QuestionBankScheme);
                policy.RequireAuthenticatedUser();
            });
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDocumentParseEndpoints();
        app.MapGet("/internal/question-bank/test", () => Results.Ok())
            .RequireAuthorization(DocLibraryAuthorizationPolicies.QuestionBank);
        app.MapGet("/health", () => Results.Ok());
        app.MapGet("/", () => Results.Ok());
        app.MapFallback(() => Results.Ok());
        await app.StartAsync();
        return app;
    }

    private static string CreateToken(string role)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("unique_name", "tester"),
                new Claim("role", role)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                SigningKey,
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
