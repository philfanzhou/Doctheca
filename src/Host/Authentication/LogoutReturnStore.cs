using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace Doctheca.Host.Authentication;

/// <summary>Bounded browser-bound one-time logout returns. They never create sessions.</summary>
public sealed class LogoutReturnStore(TimeProvider time)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    internal const int Capacity = 4096;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Binding, DateTimeOffset Expires)> _pending = [];
    internal int Count { get { lock (_gate) return _pending.Count; } }

    internal (string State, string Binding) Create()
    {
        lock (_gate)
        {
            RemoveExpiredCore();
            if (_pending.Count >= Capacity) throw new InvalidOperationException("oidc.logout_capacity");
            var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var binding = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _pending.Add(state, (binding, time.GetUtcNow() + Lifetime));
            return (state, binding);
        }
    }

    internal bool Consume(string? state, string? binding)
    {
        if (state is null || state.Length != 43) return false;
        lock (_gate)
            return _pending.Remove(state, out var item) && item.Expires > time.GetUtcNow()
                && binding is { Length: 43 }
                && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(item.Binding),
                    System.Text.Encoding.ASCII.GetBytes(binding));
    }

    internal void Remove(string state) { lock (_gate) _pending.Remove(state); }
    internal void RemoveExpired() { lock (_gate) RemoveExpiredCore(); }
    private void RemoveExpiredCore()
    {
        foreach (var key in _pending.Where(pair => pair.Value.Expires <= time.GetUtcNow()).Select(pair => pair.Key).ToArray())
            _pending.Remove(key);
    }
}
