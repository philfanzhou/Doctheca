namespace Doctheca.Domain.Models.Structure;

/// <summary>An imported audio file of one parse and its optional link to a structure node (issue #90).</summary>
public sealed class DocumentStructureAudioModel
{
    public Guid Id { get; set; }
    public Guid ParseId { get; set; }
    public Guid? NodeId { get; set; }

    /// <summary>Relative path of the file in the import, for example <c>unit1/9s01Unit1SectionA1b.mp3</c>.</summary>
    public string SourcePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Storage location; internal only and never part of a response.</summary>
    public string ObjectKey { get; set; } = string.Empty;

    /// <summary>The pre-fill outcome, one of <see cref="AudioMatchStatus"/>.</summary>
    public string MatchStatus { get; set; } = string.Empty;

    /// <summary>Who set the current link, one of <see cref="AudioLinkSource"/>; null when nobody did.</summary>
    public string? LinkSource { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The outcome of matching a file's folder and name against the node names.</summary>
public static class AudioMatchStatus
{
    /// <summary>Folder and name agree (or only one of them matched); linked to the deepest node.</summary>
    public const string Matched = "matched";

    /// <summary>Folder and name point to different nodes; left unlinked.</summary>
    public const string FolderNameMismatch = "folder_name_mismatch";

    /// <summary>Neither folder nor name matches a node; left unlinked.</summary>
    public const string NoMatch = "no_match";

    /// <summary>More than one sibling node matches; left unlinked.</summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>The linked node was removed by a later structure save; unlinked.</summary>
    public const string NodeRemoved = "node_removed";
}

public static class AudioLinkSource
{
    public const string Prefill = "prefill";
    public const string Manual = "manual";
}

/// <summary>One requested link change: the file and its new node (null clears the link).</summary>
public sealed record AudioLinkChange(Guid AudioId, Guid? NodeId);

public enum AudioLinkUpdateStatus
{
    Ok,
    ParseNotFound,
    AudioNotFound,
    NodeNotFound,
}

/// <summary>The outcome of a link save; <see cref="RemovedObjectKeys"/> are the objects to delete after commit.</summary>
public sealed record AudioLinkUpdateResult(AudioLinkUpdateStatus Status, IReadOnlyList<string> RemovedObjectKeys, Guid? MissingId = null);
