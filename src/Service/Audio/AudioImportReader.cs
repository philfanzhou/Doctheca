using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Http;
using Doctheca.Domain.Models;

namespace Doctheca.Service.Audio;

/// <summary>One rejected file of an import: its path as uploaded and the reason.</summary>
public sealed record AudioFileError(string File, string Reason);

/// <summary>A validated audio file of an import, re-readable for the storage pass.</summary>
public sealed class AudioImportEntry
{
    internal AudioImportEntry(string sourcePath, AudioFormat format, long sizeBytes, Func<Stream> open)
    {
        SourcePath = sourcePath;
        Format = format;
        SizeBytes = sizeBytes;
        Open = open;
    }

    public string SourcePath { get; }
    public AudioFormat Format { get; }
    public long SizeBytes { get; }
    public Func<Stream> Open { get; }
}

public sealed record AudioFormat(string Extension, string ContentType);

/// <summary>The outcome of reading an import: accepted files, skipped system files, and errors.</summary>
public sealed class AudioImportBatch : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public List<AudioImportEntry> Entries { get; } = [];
    public List<string> Skipped { get; } = [];
    public List<AudioFileError> Errors { get; } = [];
    public bool TooLarge { get; internal set; }
    internal long ExpandedBytes { get; set; }
    internal int ArchiveEntries { get; set; }

    internal T Own<T>(T resource) where T : IDisposable
    {
        _resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        foreach (var resource in _resources.AsEnumerable().Reverse()) resource.Dispose();
    }
}

/// <summary>
/// Reads the files of one import request (audio files and ZIP archives) and validates every one of
/// them before anything is stored: safe relative paths, supported audio content (extension and
/// file signature agree), the per-file and per-import size limits, and unique paths. Operating
/// system metadata (<c>.DS_Store</c>, <c>__MACOSX/</c>, <c>._*</c>, <c>Thumbs.db</c>,
/// <c>desktop.ini</c>) and directory entries are skipped.
/// </summary>
public static class AudioImportReader
{
    internal const int MaxPathLength = 1000;
    internal const int MaxSegmentLength = 255;

    private static readonly Dictionary<string, AudioFormat> Formats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = new(".mp3", "audio/mpeg"),
        [".m4a"] = new(".m4a", "audio/mp4"),
        [".wav"] = new(".wav", "audio/wav"),
        [".ogg"] = new(".ogg", "audio/ogg"),
        [".flac"] = new(".flac", "audio/flac"),
    };

    /// <summary>The accepted file extensions, for messages and documentation.</summary>
    public static IReadOnlyCollection<string> Extensions => Formats.Keys;

    private static readonly HashSet<string> SystemFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini",
    };

    /// <summary>
    /// Reads every uploaded file of the form. Each file's relative path is its multipart file name,
    /// or — when the form carries one <c>paths</c> value per file — that value (browsers may drop
    /// folders from file names).
    /// </summary>
    public static async Task<AudioImportBatch> ReadAsync(IFormCollection form, DocumentAudioOptions options, CancellationToken cancellationToken = default)
    {
        var files = form.Files;
        var paths = form["paths"];
        var batch = new AudioImportBatch();
        try
        {
            long total = 0;
            if (paths.Count != 0 && paths.Count != files.Count)
            {
                batch.Errors.Add(new("", "paths must contain one value per uploaded file"));
                return batch;
            }
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Length > options.MaxArchiveBytes - total) { batch.TooLarge = true; return batch; }
                total += file.Length;
                if (total > options.MaxArchiveBytes)
                {
                    batch.TooLarge = true;
                    return batch;
                }
            }

            for (var i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = files[i];
                var name = paths.Count == files.Count ? paths[i] : file.FileName;
                if (!TryNormalizePath(name, out var path, out var reason))
                {
                    batch.Errors.Add(new(name ?? string.Empty, reason));
                    continue;
                }
                if (IsSystemFile(path))
                {
                    batch.Skipped.Add(path);
                    continue;
                }

                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    await ReadArchiveAsync(batch, file, path, options, cancellationToken);
                    if (batch.TooLarge) return batch;
                    continue;
                }

                await AddEntryAsync(batch, path, file.OpenReadStream, options, cancellationToken, file.Length);
                if (batch.TooLarge) return batch;
            }

            var duplicates = batch.Entries.GroupBy(entry => entry.SourcePath, StringComparer.Ordinal).Where(group => group.Count() > 1);
            foreach (var duplicate in duplicates)
                batch.Errors.Add(new(duplicate.Key, "appears more than once in this import"));

            if (batch.Entries.Count > DocumentAudioOptions.MaxFilesPerImport)
                batch.Errors.Add(new("", $"an import may contain at most {DocumentAudioOptions.MaxFilesPerImport} audio files"));
            if (batch.Entries.Sum(entry => entry.SizeBytes) > options.MaxArchiveBytes)
                batch.TooLarge = true;
            return batch;
        }
        catch { batch.Dispose(); throw; }
    }

    private static async Task ReadArchiveAsync(AudioImportBatch batch, IFormFile file, string archivePath, DocumentAudioOptions options, CancellationToken cancellationToken)
    {
        ZipArchive archive;
        try
        {
            // Entry names without the UTF-8 flag are read as UTF-8 too (the common modern default).
            var source = batch.Own(file.OpenReadStream());
            archive = batch.Own(new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true, new UTF8Encoding(false, true)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or DecoderFallbackException)
        {
            batch.Errors.Add(new(archivePath, "is not a readable ZIP archive"));
            return;
        }

        IReadOnlyCollection<ZipArchiveEntry> entries;
        try
        {
            entries = archive.Entries;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or DecoderFallbackException)
        {
            batch.Errors.Add(new(archivePath, "is not a readable ZIP archive"));
            return;
        }

        batch.ArchiveEntries += entries.Count;
        if (batch.ArchiveEntries > DocumentAudioOptions.MaxArchiveEntries)
        {
            batch.Errors.Add(new(archivePath, $"has more than {DocumentAudioOptions.MaxFilesPerImport * 4} entries"));
            return;
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                if (!TryNormalizePath(entry.FullName.TrimEnd('/', '\\'), out _, out var directoryReason))
                    batch.Errors.Add(new($"{archivePath}: {entry.FullName}", directoryReason));
                continue;
            }

            if (!TryNormalizePath(entry.FullName, out var path, out var reason))
            {
                batch.Errors.Add(new($"{archivePath}: {entry.FullName}", reason));
                continue;
            }
            if (IsSystemFile(path))
            {
                batch.Skipped.Add($"{archivePath}: {path}");
                continue;
            }

            if (entry.Length > options.MaxArchiveBytes - batch.ExpandedBytes)
            {
                batch.TooLarge = true;
                return;
            }
            await AddEntryAsync(batch, path, entry.Open, options, cancellationToken, entry.Length);

            // Stop early on archives that hold more (real, not declared) audio than one import may.
            if (batch.Entries.Count > DocumentAudioOptions.MaxFilesPerImport) return;
            if (batch.Entries.Sum(item => item.SizeBytes) > options.MaxArchiveBytes)
            {
                batch.TooLarge = true;
                return;
            }
        }
    }

    private static async Task AddEntryAsync(AudioImportBatch batch, string path, Func<Stream> open, DocumentAudioOptions options,
        CancellationToken cancellationToken, long declaredLength = -1)
    {
        var format = Formats.GetValueOrDefault(Path.GetExtension(path));
        if (format == null)
        {
            batch.Errors.Add(new(path, $"is not a supported audio file ({string.Join(", ", Formats.Keys)})"));
            return;
        }
        if (declaredLength > options.MaxFileBytes)
        {
            batch.Errors.Add(new(path, $"is larger than the {FormatBytes(options.MaxFileBytes)} file limit"));
            return;
        }

        var header = new byte[12];
        long length;
        try
        {
            using var stream = open();
            var headerLength = await ReadAtMostAsync(stream, header, cancellationToken);
            length = headerLength;
            batch.ExpandedBytes += headerLength;
            if (batch.ExpandedBytes > options.MaxArchiveBytes) { batch.TooLarge = true; return; }
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                length += read;
                batch.ExpandedBytes += read;
                if (batch.ExpandedBytes > options.MaxArchiveBytes) { batch.TooLarge = true; return; }
                if (length > options.MaxFileBytes) break;
            }
            if (!HasSignature(format, header.AsSpan(0, headerLength)))
            {
                batch.Errors.Add(new(path, "content does not match its audio file type"));
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            batch.Errors.Add(new(path, "could not be read"));
            return;
        }

        if (length == 0)
        {
            batch.Errors.Add(new(path, "is empty"));
            return;
        }
        if (length > options.MaxFileBytes)
        {
            batch.Errors.Add(new(path, $"is larger than the {FormatBytes(options.MaxFileBytes)} file limit"));
            return;
        }
        batch.Entries.Add(new AudioImportEntry(path, format, length, open));
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0) total += read;
        return total;
    }

    internal static bool HasSignature(AudioFormat format, ReadOnlySpan<byte> header) => format.Extension switch
    {
        // ID3v2 tag, or an MPEG audio frame sync (11 set bits).
        ".mp3" => header.StartsWith("ID3"u8) || (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0),
        ".m4a" => header.Length >= 8 && header[4..8].SequenceEqual("ftyp"u8),
        ".wav" => header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),
        ".ogg" => header.StartsWith("OggS"u8),
        ".flac" => header.StartsWith("fLaC"u8),
        _ => false,
    };

    /// <summary>
    /// Accepts a relative path of folders and a file name: no absolute or drive paths, no
    /// <c>.</c>/<c>..</c> or empty segments, no control characters, bounded lengths. Backslashes
    /// count as separators; the result uses <c>/</c> and Unicode NFC.
    /// </summary>
    internal static bool TryNormalizePath(string? raw, out string path, out string reason)
    {
        path = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            reason = "has no file name";
            return false;
        }

        string candidate;
        try { candidate = raw.Replace('\\', '/').Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { reason = "has invalid Unicode in its path"; return false; }
        if (candidate.Any(c => char.IsControl(c)))
            reason = "has control characters in its path";
        else if (candidate.StartsWith('/') || candidate.Contains(':'))
            reason = "has an absolute path";
        else if (candidate.Length > MaxPathLength)
            reason = $"has a path longer than {MaxPathLength} characters";
        else
        {
            foreach (var segment in candidate.Split('/'))
            {
                if (segment.Length == 0 || segment.Trim().Length == 0)
                    reason = "has an empty path segment";
                else if (segment is "." or "..")
                    reason = "has a relative path segment ('.' or '..')";
                else if (segment.Length > MaxSegmentLength)
                    reason = $"has a path segment longer than {MaxSegmentLength} characters";
                if (reason.Length > 0) break;
            }
        }

        if (reason.Length > 0) return false;
        path = candidate;
        return true;
    }

    private static bool IsSystemFile(string path)
    {
        var segments = path.Split('/');
        var name = segments[^1];
        return segments.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase)
            || SystemFileNames.Contains(name)
            || name.StartsWith("._", StringComparison.Ordinal);
    }

    internal static string FormatBytes(long bytes) =>
        bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024)} MiB" : $"{bytes} bytes";
}
