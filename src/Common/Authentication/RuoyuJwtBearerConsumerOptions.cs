namespace Doctheca.Common.Authentication;

public sealed class RuoyuJwtBearerConsumerOptions
{
    public bool MapInboundClaims { get; set; } = true;
    public string? NameClaimType { get; set; }
    public string? RoleClaimType { get; set; }
}
