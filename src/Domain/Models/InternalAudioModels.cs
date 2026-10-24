namespace Doctheca.Domain.Models;

// Dedicated anonymous projections. Object identity stays server-side and is never serialized.
public sealed record InternalAudioObject(Guid Id, Guid? ParseId, Guid NodeId, string SourcePath,
    string ContentType, long SizeBytes, string ObjectKey, Guid DocumentFileId = default);
public sealed record InternalAudioMembership(InternalCurrentStructureNode Node, IReadOnlyList<InternalAudioObject> Items);
public sealed record InternalAudioList(Guid DocumentFileId, Guid? ParseId, Guid NodeId, IReadOnlyList<InternalAudioItem> Items);
public sealed record InternalAudioItem(Guid Id, string FileName, string ContentType, long SizeBytes, InternalAudioPlayback Playback);
public sealed record InternalAudioPlayback(string Url, DateTimeOffset ExpiresAt, int ExpiresInSeconds);
public sealed record InternalAudioBinding(Guid DocumentFileId, Guid TreeId, Guid NodeId, Guid AudioId);
