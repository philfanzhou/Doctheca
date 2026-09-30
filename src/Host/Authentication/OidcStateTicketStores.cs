using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.WebUtilities;

namespace Doctheca.Host.Authentication;

/// <summary>
/// Server-side, single-use pending-state store backing the OIDC <c>state</c> field.
/// </summary>
/// <remarks>
/// The protected value handed to the browser is an opaque random key; the real
/// <see cref="AuthenticationProperties"/> (return URL, nonce binding, correlation) never leave
/// the process. Consumption is atomic (one lock transition), entries expire after a fixed
/// lifetime, and capacity is bounded. Same design as the delivered Ruoyu.Admin slice.
/// </remarks>
public sealed class CompactStateDataFormat(TimeProvider time) : ISecureDataFormat<AuthenticationProperties>
{
    internal const int Capacity = 4096;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<string, (AuthenticationProperties Properties, DateTimeOffset Expires)> _pending = [];
    internal int Count { get { lock (_gate) return _pending.Count; } }

    public string Protect(AuthenticationProperties data) => Protect(data, null);

    public string Protect(AuthenticationProperties data, string? purpose)
    {
        lock (_gate)
        {
            RemoveExpiredCore();
            if (_pending.Count >= Capacity) throw new InvalidOperationException("oidc.state_capacity");
            string key;
            do { key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32)); } while (_pending.ContainsKey(key));
            _pending.Add(key, (new AuthenticationProperties(new Dictionary<string, string?>(data.Items)), time.GetUtcNow() + Lifetime));
            return key;
        }
    }

    public AuthenticationProperties? Unprotect(string? text) => Unprotect(text, null);

    public AuthenticationProperties? Unprotect(string? text, string? purpose)
    {
        if (text is null || text.Length != 43) return null;
        lock (_gate)
            return _pending.Remove(text, out var item) && item.Expires > time.GetUtcNow() ? item.Properties : null;
    }

    internal void RemoveExpired() { lock (_gate) RemoveExpiredCore(); }

    private void RemoveExpiredCore()
    {
        foreach (var key in _pending.Where(pair => pair.Value.Expires <= time.GetUtcNow()).Select(pair => pair.Key).ToArray())
            _pending.Remove(key);
    }
}

/// <summary>
/// In-process server-side ticket store for the opaque session cookie.
/// </summary>
/// <remarks>
/// First-stage persistence only: a restart loses every session and only a single instance is
/// supported (declared limitation, same as Ruoyu.Admin). Stored tickets are serialized
/// snapshots so a later mutation of the caller's ticket object cannot rewrite the stored one;
/// the absolute deadline never extends and is capped at the access token's own expiry, so a
/// session never outlives the access token it was issued for.
/// </remarks>
public sealed class MemoryTicketStore(TimeProvider time) : ITicketStore
{
    internal const int Capacity = 4096;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);
    private readonly object _gate = new();
    private readonly Dictionary<string, (byte[] Ticket, DateTimeOffset Deadline)> _tickets = [];
    internal int Count { get { lock (_gate) return _tickets.Count; } }

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext context, CancellationToken cancellationToken)
        => StoreAsync(ticket, cancellationToken);

    private Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            RemoveExpiredCore();
            if (_tickets.Count >= Capacity) throw new InvalidOperationException("oidc.session_capacity");
            var deadline = time.GetUtcNow() + Lifetime;
            if (ticket.Properties.ExpiresUtc is { } expires && expires < deadline) deadline = expires;
            ticket.Properties.ExpiresUtc = deadline;
            var snapshot = TicketSerializer.Default.Serialize(ticket);
            cancellationToken.ThrowIfCancellationRequested();
            var key = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            _tickets.Add(key, (snapshot, deadline));
            return Task.FromResult(key);
        }
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        lock (_gate)
        {
            if (!_tickets.TryGetValue(key, out var value)) return Task.FromResult<AuthenticationTicket?>(null);
            if (value.Deadline <= time.GetUtcNow())
            {
                _tickets.Remove(key);
                return Task.FromResult<AuthenticationTicket?>(null);
            }
            return Task.FromResult(TicketSerializer.Default.Deserialize(value.Ticket));
        }
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        lock (_gate)
        {
            if (_tickets.TryGetValue(key, out var value))
            {
                if (value.Deadline <= time.GetUtcNow()) _tickets.Remove(key);
                else
                {
                    // The absolute lifetime never extends, and Remove/Renew share the same lock.
                    ticket.Properties.ExpiresUtc = value.Deadline;
                    _tickets[key] = (TicketSerializer.Default.Serialize(ticket), value.Deadline);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key) { lock (_gate) _tickets.Remove(key); return Task.CompletedTask; }

    internal void RemoveExpired() { lock (_gate) RemoveExpiredCore(); }

    private void RemoveExpiredCore()
    {
        foreach (var key in _tickets.Where(pair => pair.Value.Deadline <= time.GetUtcNow()).Select(pair => pair.Key).ToArray())
            _tickets.Remove(key);
    }
}

/// <summary>
/// Sweeps the bounded in-process OIDC stores once a minute so expired entries release capacity.
/// </summary>
public sealed class OidcStoreCleanup(CompactStateDataFormat state, MemoryTicketStore tickets, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) { state.RemoveExpired(); tickets.RemoveExpired(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
