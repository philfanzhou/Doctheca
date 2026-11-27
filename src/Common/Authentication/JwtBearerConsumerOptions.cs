namespace Doctheca.Common.Authentication;

public sealed class JwtBearerConsumerOptions
{
    public bool MapInboundClaims { get; set; } = true;
    public string? NameClaimType { get; set; }
    public string? RoleClaimType { get; set; }
}
