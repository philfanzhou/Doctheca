using System.Globalization;
using Doctheca.Domain.Models;
using Microsoft.AspNetCore.Http;

namespace Doctheca.Service.InternalApi;

public static class InternalContentInput
{
    public static bool TryRead(IQueryCollection input, bool image, out InternalContentQuery query)
    {
        query = new(null, 200, null);
        foreach (var (key, values) in input)
        {
            if (values.Count != 1 || (key != "revision" && (image || key is not ("afterSequence" or "limit")))) return false;
            var value = values[0];
            if (key == "revision")
            {
                if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character))) return false;
                query = query with { Revision = value.ToLowerInvariant() };
            }
            else
            {
                if (string.IsNullOrEmpty(value) || !value.All(char.IsAsciiDigit) ||
                    !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return false;
                if (key == "limit")
                {
                    if (number is < 1 or > 1000) return false;
                    query = query with { Limit = number };
                }
                else query = query with { AfterSequence = number };
            }
        }
        return (query.AfterSequence is null && !image) || query.Revision is not null;
    }
}
