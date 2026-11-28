using System.IO;
using System.Threading.Tasks;

namespace Doctheca.Common.ObjectStorage;

/// <summary>Keeps objects as files under one root directory; the object key is the relative path.</summary>
public class LocalFileStore : IObjectStore
{
    private readonly string _rootPath;

    public LocalFileStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
    }

    public Task<string> UploadAsync(string objectPath, Stream content, string contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = GetFullPath(objectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        content.CopyTo(fs);
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
}
