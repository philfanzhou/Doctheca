using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Doctheca.Common.ObjectStorage;

public class LocalFileStore : IObjectStore
{
    private readonly string _rootPath;

    public LocalFileStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
        foreach (Bucket bucket in Enum.GetValues(typeof(Bucket)))
        {
            var prefix = BucketToPrefix(bucket);
            Directory.CreateDirectory(Path.Combine(_rootPath, prefix));
        }
    }

    public Task<string> UploadAsync(Stream stream, string fileName, string contentType, Bucket bucket = Bucket.Uploads, string? folder = null)
    {
        var objectPath = GenerateObjectPath(fileName, bucket, folder);
        var fullPath = GetFullPath(objectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        stream.CopyTo(fs);
        return Task.FromResult(objectPath);
    }

    public Task<Stream> DownloadAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Object not found: {objectPath}", fullPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return Task.FromResult<Stream>(stream);
    }

    public Task<bool> ObjectExistsAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task<bool> DeleteAsync(string objectPath)
    {
        var fullPath = GetFullPath(objectPath);
        if (!File.Exists(fullPath))
            return Task.FromResult(false);
        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600)
    {
        var fullPath = GetFullPath(objectPath);
        return Task.FromResult(fullPath);
    }

    private string GetFullPath(string objectPath)
    {
        var normalized = objectPath.Replace('\\', '/').TrimStart('/');
        return Path.GetFullPath(Path.Combine(_rootPath, normalized));
    }

    private static string BucketToPrefix(Bucket bucket) => bucket switch
    {
        Bucket.Uploads => "uploads",
        Bucket.Mistakes => "mistakes",
        Bucket.Questions => "questions",
        Bucket.Documents => "documents",
        Bucket.Audio => "audio",
        _ => "others"
    };

    private static string GenerateObjectPath(string fileName, Bucket bucket, string? folder = null)
    {
        var prefix = BucketToPrefix(bucket);
        if (!string.IsNullOrWhiteSpace(folder))
            return $"{prefix}/{folder}/{fileName}";
        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{now.Day:D2}/{Guid.NewGuid():N}/{fileName}";
    }
}
