using System.Security.Cryptography;
using Doctheca.Domain.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Doctheca.Service.InternalApi;

/// <summary>Process-local playback capabilities; independent of the application's administrative key ring.</summary>
public sealed class InternalAudioTokens
{
    public const string Purpose = "Doctheca.InternalAudio.v2";
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeProvider _clock;
    public InternalAudioTokens(TimeProvider clock) : this(new EphemeralDataProtectionProvider(), clock) { }
    internal InternalAudioTokens(IDataProtectionProvider provider, TimeProvider clock)
    { _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector(); _clock = clock; }

    public string Create(InternalAudioBinding binding, DateTimeOffset expiry)
    {
        var payload = new byte[65];
        payload[0] = 2;
        binding.DocumentFileId.TryWriteBytes(payload.AsSpan(1, 16));
        binding.TreeId.TryWriteBytes(payload.AsSpan(17, 16));
        binding.NodeId.TryWriteBytes(payload.AsSpan(33, 16));
        binding.AudioId.TryWriteBytes(payload.AsSpan(49, 16));
        return WebEncoders.Base64UrlEncode(_protector.Protect(payload, expiry));
    }

    public bool TryRead(string? token, Guid node, Guid audio, out InternalAudioBinding? binding)
    {
        binding = null;
        if (string.IsNullOrEmpty(token) || token.Length > 2048) return false;
        try
        {
            var protectedBytes = WebEncoders.Base64UrlDecode(token);
            if (WebEncoders.Base64UrlEncode(protectedBytes) != token) return false;
            var bytes = _protector.Unprotect(protectedBytes, out var expiry);
            if (bytes.Length != 65 || bytes[0] != 2 || expiry <= _clock.GetUtcNow()) return false;
            var value = new InternalAudioBinding(new(bytes.AsSpan(1, 16)), new(bytes.AsSpan(17, 16)),
                new(bytes.AsSpan(33, 16)), new(bytes.AsSpan(49, 16)));
            if (value.NodeId != node || value.AudioId != audio) return false;
            binding = value;
            return true;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        { return false; }
    }
}

public sealed record InternalAudioSettings(bool Available, int LinkSeconds)
{
    public static InternalAudioSettings Evaluate(DocumentAudioStorageStatus storage, int seconds) =>
        new(storage.Available, seconds);
}
