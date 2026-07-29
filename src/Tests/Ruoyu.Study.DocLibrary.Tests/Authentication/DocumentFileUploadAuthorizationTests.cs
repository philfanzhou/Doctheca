using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Ruoyu.Study.Common.Oss;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.Authentication;

public class DocumentFileUploadAuthorizationTests
{
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("doclibrary-upload-test-key-32bytes!"));

    [Fact]
    public async Task Upload_AdministratorSubject_IsStoredAsCreatedBy()
    {
        var accountId = Guid.NewGuid();
        DocumentFileModel? captured = null;
        var fileService = new Mock<IDocumentFileService>();
        fileService.Setup(x => x.CreateAsync(It.IsAny<DocumentFileModel>()))
            .Callback<DocumentFileModel>(model => captured = model)
            .ReturnsAsync((DocumentFileModel model) => model);
        var ossService = new Mock<IOssService>();
        ossService.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                "application/pdf",
                OssBucket.Documents,
                "doclibrary-files"))
            .ReturnsAsync("documents/test.pdf");
        await using var app = await CreateAppAsync(fileService.Object, ossService.Object);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "paper.pdf");
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/document-files/upload")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(accountId.ToString()));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().NotBeNull();
        captured!.CreatedBy.Should().Be(accountId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task Upload_MissingOrInvalidSubject_KeepsCreatedByNull(string? subject)
    {
        DocumentFileModel? captured = null;
        var fileService = new Mock<IDocumentFileService>();
        fileService.Setup(x => x.CreateAsync(It.IsAny<DocumentFileModel>()))
            .Callback<DocumentFileModel>(model => captured = model)
            .ReturnsAsync((DocumentFileModel model) => model);
        var ossService = new Mock<IOssService>();
        ossService.Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                "application/pdf",
                OssBucket.Documents,
                "doclibrary-files"))
            .ReturnsAsync("documents/test.pdf");
        await using var app = await CreateAppAsync(fileService.Object, ossService.Object);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([1]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "paper.pdf");
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/document-files/upload")
        {
            Content = form
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(subject));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured!.CreatedBy.Should().BeNull();
    }

    private static async Task<WebApplication> CreateAppAsync(
        IDocumentFileService fileService,
        IOssService ossService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fileService);
        builder.Services.AddSingleton(ossService);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = SigningKey,
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(DocLibraryAuthorizationPolicies.Admin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.RequireClaim("role", "admin");
            });
        });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        var group = app.MapGroup("/admin/document-files")
            .RequireAuthorization(DocLibraryAuthorizationPolicies.Admin);
        group.MapUpload();
        await app.StartAsync();
        return app;
    }

    private static string CreateToken(string? subject)
    {
        var claims = new List<Claim> { new("role", "admin") };
        if (subject != null)
        {
            claims.Add(new Claim("sub", subject));
        }
        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                SigningKey,
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
