using System.Security.Claims;
using Doctheca.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Xunit;

namespace Doctheca.Tests.Authentication;

/// <summary>
/// Unit tests of the in-process session ticket store: bounded capacity with least-recently-used
/// eviction (the declared trade-off against the package's refuse-when-full policy), expiry
/// reclamation on access and from the sweep, renewal semantics, and opaque unguessable keys.
/// </summary>
public sealed class AdminOidcTicketStoreTests
{
    private static AuthenticationTicket Ticket(DateTimeOffset? expires = null) => new(
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("role", "admin")], "test", nameType: null, roleType: "role")),
        new AuthenticationProperties { ExpiresUtc = expires ?? DateTimeOffset.UtcNow.AddMinutes(15) },
        AdminOidcConstants.SessionScheme);

    [Fact]
    public async Task StoresAndRetrievesTicketsUnderOpaqueKeys()
    {
        var store = new AdminOidcTicketStore();
        var key = await store.StoreAsync(Ticket());
        Assert.NotEmpty(key);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", key);
        var retrieved = await store.RetrieveAsync(key);
        Assert.NotNull(retrieved);
        Assert.True(retrieved!.Principal.IsInRole("admin"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task UnknownMissingAndEmptyKeysAnswerNull()
    {
        var store = new AdminOidcTicketStore();
        Assert.Null(await store.RetrieveAsync("no-such-key"));
        Assert.Null(await store.RetrieveAsync(""));
        await store.RemoveAsync("no-such-key");
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task FullStoreEvictsLeastRecentlyUsedInsteadOfRefusingNewSessions()
    {
        var store = new AdminOidcTicketStore(capacity: 2);
        var first = await store.StoreAsync(Ticket());
        var second = await store.StoreAsync(Ticket());
        // Touch the oldest ticket so the middle one becomes the eviction victim.
        Assert.NotNull(await store.RetrieveAsync(first));
        var third = await store.StoreAsync(Ticket());
        Assert.Equal(2, store.Count);
        Assert.NotNull(await store.RetrieveAsync(first));
        Assert.Null(await store.RetrieveAsync(second));
        Assert.NotNull(await store.RetrieveAsync(third));
    }

    [Fact]
    public async Task ExpiredTicketIsReclaimedOnAccess()
    {
        var time = new ManualOidcTime();
        var store = new AdminOidcTicketStore(time);
        var key = await store.StoreAsync(Ticket(DateTimeOffset.UtcNow.AddSeconds(30)));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await store.RetrieveAsync(key));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task SweepRemovesOnlyExpiredTickets()
    {
        var time = new ManualOidcTime();
        var store = new AdminOidcTicketStore(time);
        var live = await store.StoreAsync(Ticket(DateTimeOffset.UtcNow.AddMinutes(5)));
        var dead = await store.StoreAsync(Ticket(DateTimeOffset.UtcNow.AddSeconds(30)));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, store.RemoveExpired());
        Assert.Equal(1, store.Count);
        Assert.NotNull(await store.RetrieveAsync(live));
        Assert.Null(await store.RetrieveAsync(dead));
        Assert.Equal(0, store.RemoveExpired());
    }

    [Fact]
    public async Task RenewReplacesTicketUnderSameKeyAndUnknownKeyDegradesToStore()
    {
        var store = new AdminOidcTicketStore();
        var key = await store.StoreAsync(Ticket());
        var replacement = Ticket();
        await store.RenewAsync(key, replacement);
        Assert.Equal(1, store.Count);
        Assert.Same(replacement, await store.RetrieveAsync(key));

        await store.RenewAsync("unknown-key", Ticket());
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task RemoveDeletesTheTicket()
    {
        var store = new AdminOidcTicketStore();
        var key = await store.StoreAsync(Ticket());
        await store.RemoveAsync(key);
        Assert.Equal(0, store.Count);
        Assert.Null(await store.RetrieveAsync(key));
    }
}
