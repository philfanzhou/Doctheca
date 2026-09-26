namespace Doctheca.Host.Authentication;

public sealed class DocthecaCookieOptions
{
    public const string SectionName = "Authentication";

    public bool CookieSecure { get; set; }
}
