using Microsoft.Extensions.Configuration;

namespace Doctheca.Service.InternalApi;

public sealed record InternalApiOptions(bool Enabled, bool Invalid)
{
    public static InternalApiOptions Read(IConfiguration configuration)
    {
        var raw = configuration["InternalApi:Enabled"];
        return raw is null ? new(false, false) : bool.TryParse(raw, out var enabled)
            ? new(enabled, false) : new(false, true);
    }
}
