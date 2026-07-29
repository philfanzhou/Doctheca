using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ruoyu.Study.DocLibrary.Host.Authentication;
using Xunit;

namespace Ruoyu.Study.DocLibrary.Tests.Authentication;

public class IdentityTokenValidatorTests : IDisposable
{
    private const string Issuer = "QuantumZhou.Identity";
    private const string Audience = "QuantumZhou.microservices";
    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RSA _otherRsa = RSA.Create(2048);

    [Theory]
    [InlineData(TokenMutation.WrongSignature)]
    [InlineData(TokenMutation.WrongIssuer)]
    [InlineData(TokenMutation.WrongAudience)]
    [InlineData(TokenMutation.Expired)]
    public async Task ValidateTokenAsync_CryptographicallyInvalidToken_IsRejected(TokenMutation mutation)
    {
        var validator = CreateValidator();
        var token = CreateToken(mutation);

        var action = () => validator.ValidateTokenAsync(token, CancellationToken.None);

        await action.Should().ThrowAsync<SecurityTokenValidationException>();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("user", false)]
    [InlineData("admin", true)]
    [InlineData("AdMiN", true)]
    public async Task ValidateTokenAsync_ValidToken_ReportsAdministratorRole(
        string? role,
        bool expectedAdministrator)
    {
        var validator = CreateValidator();
        var token = CreateToken(TokenMutation.None, role);

        var result = await validator.ValidateTokenAsync(token, CancellationToken.None);

        result.UserId.Should().Be("11111111-1111-1111-1111-111111111111");
        result.Username.Should().Be("admin");
        result.IsAdministrator.Should().Be(expectedAdministrator);
        result.ExpiresAt.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private IdentityTokenValidator CreateValidator()
    {
        var signingKey = new RsaSecurityKey(_rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer
        };
        configuration.SigningKeys.Add(signingKey);
        var manager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        return new IdentityTokenValidator(
            manager,
            Options.Create(new IdentityServiceOptions
            {
                Issuer = Issuer,
                Audience = Audience
            }));
    }

    private string CreateToken(TokenMutation mutation, string? role = "admin")
    {
        var signingRsa = mutation == TokenMutation.WrongSignature ? _otherRsa : _rsa;
        var key = new RsaSecurityKey(signingRsa) { KeyId = "test-key" };
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", "11111111-1111-1111-1111-111111111111"),
            new("unique_name", "admin")
        };
        if (role != null)
        {
            claims.Add(new Claim("role", role));
        }

        var token = new JwtSecurityToken(
            issuer: mutation == TokenMutation.WrongIssuer ? "wrong-issuer" : Issuer,
            audience: mutation == TokenMutation.WrongAudience ? "wrong-audience" : Audience,
            claims: claims,
            notBefore: now.AddMinutes(-5),
            expires: mutation == TokenMutation.Expired ? now.AddMinutes(-2) : now.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherRsa.Dispose();
    }

    public enum TokenMutation
    {
        None,
        WrongSignature,
        WrongIssuer,
        WrongAudience,
        Expired
    }
}
