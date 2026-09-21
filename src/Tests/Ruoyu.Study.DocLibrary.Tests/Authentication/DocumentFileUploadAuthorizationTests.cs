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
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Ruoyu.Study.DocLibrary.Domain.Models;
using Ruoyu.Study.DocLibrary.Domain.Services;
using Ruoyu.Study.DocLibrary.Service;
using Ruoyu.Study.DocLibrary.Service.StructaDoc;
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
        var structaDocDocumentId = Guid.NewGuid();
        DocumentFileModel? captured = null;
        var fileService = new Mock<IDocumentFileService>();
        fileService.Setup(x => x.CreateAsync(It.IsAny<DocumentFileModel>()))
            .Callback<DocumentFileModel>(model => captured = model)
            .ReturnsAsync((DocumentFileModel model) => model);
        var structaDocClient = new Mock<IStructaDocClient>();
        structaDocClient.Setup(x => x.UploadDocumentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructaDocDocumentResponse
            {
                Id = structaDocDocumentId,
                MediaType = "application/pdf",
            });
        await using var app = await CreateAppAsync(fileService.Object, structaDocClient.Object);
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
        captured.StructaDocDocumentId.Should().Be(structaDocDocumentId);
        captured.FilePath.Should().BeNull();
        captured.ContentType.Should().Be("application/pdf");
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
        var structaDocClient = new Mock<IStructaDocClient>();
        structaDocClient.Setup(x => x.UploadDocumentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StructaDocDocumentResponse
            {
                Id = Guid.NewGuid(),
                MediaType = "application/pdf",
            });
        await using var app = await CreateAppAsync(fileService.Object, structaDocClient.Object);
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

    [Fact]
    public async Task Upload_StructaDocNotConfigured_Returns503()
    {
        var fileService = new Mock<IDocumentFileService>();
        var structaDocClient = new Mock<IStructaDocClient>();
        await using var app = await CreateAppAsync(
            fileService.Object, structaDocClient.Object, configured: false);
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
            CreateToken(Guid.NewGuid().ToString()));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        structaDocClient.Verify(
            x => x.UploadDocumentAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<WebApplication> CreateAppAsync(
        IDocumentFileService fileService,
        IStructaDocClient structaDocClient,
        bool configured = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fileService);
        builder.Services.AddSingleton(structaDocClient);
        builder.Services.AddSingleton(Options.Create(configured
            ? new StructaDocOptions { BaseUrl = "http://structadoc.test", ApiKey = "sd1.test.key" }
            : new StructaDocOptions()));
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
