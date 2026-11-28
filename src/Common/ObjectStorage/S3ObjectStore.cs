using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Minio;
using Minio.DataModel.Args;

namespace Doctheca.Common.ObjectStorage;

/// <summary>
/// Object storage operations Doctheca relies on. Every method addresses an object by its full key
/// inside the configured bucket (or root directory).
/// </summary>
public interface IObjectStore
{
    /// <summary>Stores <paramref name="content"/> under the exact <paramref name="objectPath"/> and returns that key.</summary>
    Task<string> UploadAsync(string objectPath, Stream content, string contentType, CancellationToken cancellationToken);
    Task<Stream> DownloadAsync(string objectPath);
    /// <summary>Cancellable byte transfer; unsupported providers must not fall back to legacy unbounded buffering.</summary>
    Task CopyToAsync(string objectPath, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Cancellable object transfer is not supported by this provider.");
    Task<bool> ObjectExistsAsync(string objectPath);
    async Task<bool> ObjectExistsAsync(string objectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exists = await ObjectExistsAsync(objectPath);
        cancellationToken.ThrowIfCancellationRequested();
        return exists;
    }
    Task<bool> DeleteAsync(string objectPath);
    Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600);
    async Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var url = await GetPresignedUrlAsync(objectPath, expirySeconds);
        cancellationToken.ThrowIfCancellationRequested();
        return url;
    }
    /// <summary>Returns a complete listing or throws; unsupported providers must not report an empty success.</summary>
    Task<List<ObjectInfo>> ListObjectsOrThrowAsync(string? prefix = null) =>
        throw new NotSupportedException("Reliable object listing is not supported by this provider.");
}

public class ObjectInfo
{
    public string ObjectPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

public class S3ObjectStore : IObjectStore
{
    private readonly IMinioClient _internalClient;
    private readonly Lazy<IMinioClient> _presignClient;
    private readonly Lazy<IMinioClient> _audioPresignClient;
    private readonly string _bucketName;
    private readonly Lazy<Uri?> _publicBaseUri;

    public S3ObjectStore(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _bucketName = options.BucketName;
        _internalClient = BuildClient(
            options.InternalEndpoint,
            options.AccessKey,
            options.SecretKey,
            options.InternalSecure);

        // Invalid public playback settings must not disable internal uploads, reads or cleanup.
        var publicBaseUrl = options.PublicBaseUrl;
        var accessKey = options.AccessKey;
        var secretKey = options.SecretKey;
        _publicBaseUri = new(() => ParsePublicBaseUri(publicBaseUrl));
        _presignClient = new(() => _publicBaseUri.Value == null
            ? _internalClient
            : BuildClient(_publicBaseUri.Value.Authority, accessKey, secretKey,
                string.Equals(_publicBaseUri.Value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)));
        _audioPresignClient = new(() => _publicBaseUri.Value is { } publicUri
            ? BuildClient(publicUri.Authority, accessKey, secretKey,
                string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase), publicSigner: true)
            : throw new InvalidOperationException("Public audio playback is not configured."));
    }

    private static IMinioClient BuildClient(string endpoint, string accessKey, string secretKey, bool secure, bool publicSigner = false)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new ArgumentException("The S3 endpoint is required.", nameof(endpoint));
        }

        var client = new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(secure);
        // Signing must not make region-discovery calls through the browser-facing endpoint.
        if (publicSigner) client = client.WithRegion("us-east-1");
        return client.Build();
    }

    public async Task<string> UploadAsync(string objectPath, Stream content, string contentType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureBucketExistsAsync(cancellationToken);

        var putObjectArgs = new PutObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithStreamData(content)
            .WithObjectSize(content.Length)
            .WithContentType(contentType);

        await _internalClient.PutObjectAsync(putObjectArgs, cancellationToken);

        return objectPath;
    }

    public async Task<Stream> DownloadAsync(string objectPath)
    {
        var memoryStream = new MemoryStream();

        var getObjectArgs = new GetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithCallbackStream(stream =>
            {
                stream.CopyTo(memoryStream);
                memoryStream.Position = 0;
            });

        await _internalClient.GetObjectAsync(getObjectArgs);

        return memoryStream;
    }

    public async Task CopyToAsync(string objectPath, Stream destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var completed = false;
        var args = new GetObjectArgs().WithBucket(_bucketName).WithObject(objectPath)
            .WithCallbackStream(async (source, sdkCancellation) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sdkCancellation);
                await using (source) await source.CopyToAsync(destination, linked.Token);
                completed = true;
            });
        await _internalClient.GetObjectAsync(args, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!completed) throw new IOException("Object transfer did not complete.");
    }

    public Task<bool> ObjectExistsAsync(string objectPath) => ObjectExistsAsync(objectPath, CancellationToken.None);

    public async Task<bool> ObjectExistsAsync(string objectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var statArgs = new StatObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            var stat = await _internalClient.StatObjectAsync(statArgs, cancellationToken);
            return stat != null && stat.Size > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteAsync(string objectPath)
    {
        try
        {
            // If not a thumbnail path, delete the associated thumbnails first
            if (!ThumbnailHelper.IsThumbnailPath(objectPath))
            {
                var thumbnailPaths = ThumbnailHelper.GetAllThumbnailPaths(objectPath);
                foreach (var thumbPath in thumbnailPaths)
                {
                    try
                    {
                        var removeThumbArgs = new RemoveObjectArgs()
                            .WithBucket(_bucketName)
                            .WithObject(thumbPath);
                        await _internalClient.RemoveObjectAsync(removeThumbArgs);
                    }
                    catch
                    {
                        // Ignore when the thumbnail does not exist
                    }
                }
            }

            var removeObjectArgs = new RemoveObjectArgs()
                .WithBucket(_bucketName)
                .WithObject(objectPath);

            await _internalClient.RemoveObjectAsync(removeObjectArgs);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds = 3600) =>
        PresignAsync(objectPath, expirySeconds, null, CancellationToken.None);

    /// <summary>Audio signing requires the explicit public client and one exact expiry timestamp.</summary>
    public Task<string> GetPresignedUrlAsync(string objectPath, int expirySeconds, DateTimeOffset issuedAt, CancellationToken cancellationToken) =>
        PresignAsync(objectPath, expirySeconds, issuedAt, cancellationToken);

    private async Task<string> PresignAsync(string objectPath, int expirySeconds, DateTimeOffset? issuedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var presignedGetObjectArgs = new PresignedGetObjectArgs()
            .WithBucket(_bucketName)
            .WithObject(objectPath)
            .WithExpiry(expirySeconds);
        if (issuedAt is { } timestamp) presignedGetObjectArgs.WithRequestDate(timestamp.UtcDateTime);
        // Preserve the legacy presigner's region discovery; audio never uses it or its fallback.
        var client = issuedAt != null ? _audioPresignClient.Value : _presignClient.Value;
        var url = await client.PresignedGetObjectAsync(presignedGetObjectArgs);

        if (_publicBaseUri.Value != null && !string.IsNullOrEmpty(url))
        {
            url = PresignedUrlPathPrefixer.AddPrefix(url, _publicBaseUri.Value);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return url;
    }

    public async Task<List<ObjectInfo>> ListObjectsOrThrowAsync(string? prefix = null)
    {
        var listArgs = new ListObjectsArgs()
            .WithBucket(_bucketName)
            .WithPrefix(prefix)
            .WithRecursive(true);

        var objects = new List<ObjectInfo>();
        await foreach (var item in _internalClient.ListObjectsEnumAsync(listArgs))
        {
            objects.Add(new ObjectInfo
            {
                ObjectPath = item.Key,
                Size = (long)item.Size,
                LastModified = string.IsNullOrEmpty(item.LastModified) ? null : DateTimeOffset.TryParse(item.LastModified, out var dt) ? (DateTimeOffset?)dt : null
            });
        }
        return objects;
    }

    private async Task EnsureBucketExistsAsync(CancellationToken cancellationToken = default)
    {
        var beArgs = new BucketExistsArgs().WithBucket(_bucketName);
        if (!await _internalClient.BucketExistsAsync(beArgs, cancellationToken))
        {
            var mbArgs = new MakeBucketArgs().WithBucket(_bucketName);
            try { await _internalClient.MakeBucketAsync(mbArgs, cancellationToken); }
            catch (ArgumentException ex) when (ex.ParamName == "response")
            {
                // MinIO maps BucketAlreadyOwnedByYou to ArgumentException. Another request may
                // have created the bucket after our check; confirm it exists before continuing.
                if (!await _internalClient.BucketExistsAsync(beArgs, cancellationToken)) throw;
            }
        }
    }

    private static Uri? ParsePublicBaseUri(string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(publicBaseUrl.TrimEnd('/'), UriKind.Absolute, out var publicBaseUri) ||
            (publicBaseUri.Scheme != Uri.UriSchemeHttp && publicBaseUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(publicBaseUri.Query) ||
            !string.IsNullOrEmpty(publicBaseUri.Fragment) ||
            !string.IsNullOrEmpty(publicBaseUri.UserInfo))
        {
            throw new ArgumentException(
                "Oss:PublicBaseUrl must be an absolute HTTP or HTTPS URL without query, fragment, or user information.",
                nameof(publicBaseUrl));
        }

        return publicBaseUri;
    }
}

public class StoreOptions
{
    public string InternalEndpoint { get; set; } = "localhost:8333";
    public bool InternalSecure { get; set; }
    public string AccessKey { get; set; } = "seaweedfs_admin";
    public string SecretKey { get; set; } = "seaweedfs_admin";
    public string BucketName { get; set; } = "ruoyu-study";
    public string? PublicBaseUrl { get; set; }
}
