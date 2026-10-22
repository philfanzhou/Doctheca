using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Minio;
using Minio.DataModel.Args;
using Xunit;

namespace Doctheca.Tests.Integration;

/// <summary>
/// A real S3-compatible store (the MinIO community build) for the structure audio tests. The
/// credentials are synthetic per-run values; nothing here reaches a real storage endpoint.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string Image = "pgsty/minio:RELEASE.2026-08-04T00-00-00Z";

    private readonly IContainer _container;

    public MinioFixture()
    {
        _container = new ContainerBuilder(Image)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithCommand("server", "/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(9000).ForPath("/minio/health/live")))
            .Build();
    }

    public string AccessKey { get; } = "test" + Guid.NewGuid().ToString("N")[..12];
    public string SecretKey { get; } = Guid.NewGuid().ToString("N");
    public int Port => _container.GetMappedPublicPort(9000);

    /// <summary>The endpoint the server talks to; it must never appear in a response.</summary>
    public string InternalEndpoint => $"127.0.0.1:{Port}";

    /// <summary>The public base of playback links (another name for the same container).</summary>
    public string PublicBaseUrl => $"http://localhost:{Port}";

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Settings that enable structure audio against this store with the given bucket.</summary>
    public Dictionary<string, string?> Settings(string bucket) => new()
    {
        ["DocumentAudio:Enabled"] = "true",
        ["Oss:InternalEndpoint"] = InternalEndpoint,
        ["Oss:InternalSecure"] = "false",
        ["Oss:AccessKey"] = AccessKey,
        ["Oss:SecretKey"] = SecretKey,
        ["Oss:BucketName"] = bucket,
        ["Oss:PublicBaseUrl"] = PublicBaseUrl,
    };

    public IMinioClient Client() => new MinioClient()
        .WithEndpoint(InternalEndpoint)
        .WithCredentials(AccessKey, SecretKey)
        .WithSSL(false)
        .Build();

    /// <summary>Every object key of a bucket (empty when the bucket does not exist yet).</summary>
    public async Task<List<string>> KeysAsync(string bucket)
    {
        using var client = Client();
        if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket))) return [];
        var keys = new List<string>();
        await foreach (var item in client.ListObjectsEnumAsync(new ListObjectsArgs().WithBucket(bucket).WithRecursive(true)))
            keys.Add(item.Key);
        return keys;
    }

    public async Task PutAsync(string bucket, string key, byte[] content)
    {
        using var client = Client();
        if (!await client.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucket)))
            await client.MakeBucketAsync(new MakeBucketArgs().WithBucket(bucket));
        using var stream = new MemoryStream(content);
        await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key)
            .WithStreamData(stream).WithObjectSize(content.Length).WithContentType("audio/mpeg"));
    }
}
