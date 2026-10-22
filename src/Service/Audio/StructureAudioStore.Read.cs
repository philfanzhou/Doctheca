using Doctheca.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Doctheca.Service.Audio;

public sealed partial class StructureAudioStore
{
    /// <summary>Buffers exactly one validated object into request-owned bounded seekable scratch space.</summary>
    public async Task<Stream> BufferAsync(InternalAudioObject audio, long maxBytes, AudioScratchSpace scratch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream? stream = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var extension = Path.GetExtension(audio.ObjectKey);
            var expectedType = extension switch
            {
                ".mp3" => "audio/mpeg", ".m4a" => "audio/mp4", ".wav" => "audio/wav",
                ".ogg" => "audio/ogg", ".flac" => "audio/flac", _ => null,
            };
            if (!Status.Available || audio.SizeBytes <= 0 || audio.SizeBytes > maxBytes || expectedType is null ||
                audio.ContentType != expectedType || audio.ObjectKey != $"{ParsePrefix(audio.ParseId)}{audio.Id:N}{extension}")
                throw new AudioStorageException();
            stream = scratch.Open();
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek || stream.Length != 0) throw new AudioStorageException();
            using var bounded = new BoundedAudioDestination(stream, audio.SizeBytes);
            await _ossService.Value.CopyToAsync(audio.ObjectKey, bounded, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (bounded.Exceeded || bounded.Written != audio.SizeBytes || stream.Length != audio.SizeBytes) throw new AudioStorageException();
            stream.Position = 0;
            return stream;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            if (stream is not null) await stream.DisposeAsync();
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            if (stream is not null) await stream.DisposeAsync();
            _logger.LogWarning("Internal audio storage read failed.");
            throw new AudioStorageException();
        }
    }

    private sealed class BoundedAudioDestination(Stream target, long limit) : Stream
    {
        public long Written { get; private set; }
        public bool Exceeded { get; private set; }
        private void Check(int count) { if (count > limit - Written) { Exceeded = true; throw new AudioStorageException(); } }
        public override void Write(byte[] buffer, int offset, int count)
        { Check(count); target.Write(buffer, offset, count); Written += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Check(buffer.Length); await target.WriteAsync(buffer, cancellationToken); Written += buffer.Length; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => target.FlushAsync(cancellationToken);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
