using Amazon.S3;
using Amazon.S3.Model;

namespace Erp.Core.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>S3 endpoint, e.g. <c>https://s3.fr-par.scw.cloud</c> (Scaleway) or <c>http://localhost:9000</c> (MinIO).</summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Region the bucket lives in, e.g. <c>fr-par</c>.</summary>
    public string Region { get; set; } = "fr-par";

    public string Bucket { get; set; } = "erp-documents";
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }

    /// <summary>Path-style addressing (required by MinIO).</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Create the bucket at startup when it is missing, with Object Lock enabled. Intended for local development and
    /// tests; in production the bucket is created by infrastructure code (Object Lock can only be enabled at creation).
    /// </summary>
    public bool CreateBucketIfMissing { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(AccessKey);
}

/// <summary>The object is protected by a retention lock and cannot be deleted or overwritten yet.</summary>
public sealed class ObjectLockedException(string key, Exception? inner = null)
    : Exception($"Object '{key}' is protected by a retention lock.", inner);

/// <summary>
/// Object store for file content. The platform stores files through this contract; the S3 adapter works against
/// Scaleway Object Storage in production and MinIO locally (ADR-0012).
/// </summary>
public interface IObjectStorage
{
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Returns the content, or <c>null</c> when the key does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the object. Throws <see cref="ObjectLockedException"/> while a retention lock applies.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the object against deletion and overwrite until the given time (Object Lock, compliance mode: no one,
    /// including administrators, can shorten it). A later date extends an existing lock; an earlier one is ignored.
    /// </summary>
    Task LockUntilAsync(string key, DateTimeOffset retainUntil, CancellationToken cancellationToken = default);

    Task<DateTimeOffset?> GetLockedUntilAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly ObjectStorageOptions _options;
    private readonly AmazonS3Client _client;
    private readonly SemaphoreSlim _ensureBucket = new(1, 1);
    private bool _bucketReady;

    public S3ObjectStorage(ObjectStorageOptions options)
    {
        _options = options;
        _client = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(options.AccessKey, options.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = options.ServiceUrl,
                AuthenticationRegion = options.Region,
                ForcePathStyle = options.ForcePathStyle
            });
    }

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        using var stream = new MemoryStream(content, writable: false);
        try
        {
            await _client.PutObjectAsync(
                new PutObjectRequest
                {
                    BucketName = _options.Bucket,
                    Key = key,
                    InputStream = stream,
                    ContentType = contentType,
                    DisablePayloadSigning = !_options.ServiceUrl!.StartsWith("https", StringComparison.OrdinalIgnoreCase)
                },
                cancellationToken);
        }
        catch (AmazonS3Exception ex) when (IsLocked(ex))
        {
            throw new ObjectLockedException(key, ex);
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        try
        {
            using var response = await _client.GetObjectAsync(_options.Bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        try
        {
            // On a versioned bucket (required for Object Lock) a plain delete only adds a delete marker. Delete the
            // concrete versions so the content is really removed, which the retention lock refuses while it applies.
            var versions = await _client.ListVersionsAsync(
                new ListVersionsRequest { BucketName = _options.Bucket, Prefix = key }, cancellationToken);
            foreach (var version in versions.Versions.Where(v => v.Key == key))
            {
                await _client.DeleteObjectAsync(
                    new DeleteObjectRequest { BucketName = _options.Bucket, Key = key, VersionId = version.VersionId },
                    cancellationToken);
            }
        }
        catch (AmazonS3Exception ex) when (IsLocked(ex))
        {
            throw new ObjectLockedException(key, ex);
        }
    }

    public async Task LockUntilAsync(string key, DateTimeOffset retainUntil, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        var current = await GetLockedUntilAsync(key, cancellationToken);
        if (current is not null && current >= retainUntil)
        {
            return;
        }

        await _client.PutObjectRetentionAsync(
            new PutObjectRetentionRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                Retention = new ObjectLockRetention
                {
                    Mode = ObjectLockRetentionMode.Compliance,
                    RetainUntilDate = retainUntil.UtcDateTime
                }
            },
            cancellationToken);
    }

    public async Task<DateTimeOffset?> GetLockedUntilAsync(string key, CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken);
        try
        {
            var response = await _client.GetObjectRetentionAsync(
                new GetObjectRetentionRequest { BucketName = _options.Bucket, Key = key }, cancellationToken);
            return response.Retention?.RetainUntilDate is { } until
                ? new DateTimeOffset(DateTime.SpecifyKind(until, DateTimeKind.Utc))
                : null;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchObjectLockConfiguration" or "InvalidRequest"
                                           || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static bool IsLocked(AmazonS3Exception ex) =>
        ex.StatusCode == System.Net.HttpStatusCode.Forbidden
        || ex.ErrorCode is "AccessDenied" or "ObjectLocked";

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_bucketReady || !_options.CreateBucketIfMissing)
        {
            return;
        }

        await _ensureBucket.WaitAsync(cancellationToken);
        try
        {
            if (_bucketReady)
            {
                return;
            }

            var buckets = await _client.ListBucketsAsync(cancellationToken);
            if (!buckets.Buckets.Any(b => b.BucketName == _options.Bucket))
            {
                await _client.PutBucketAsync(
                    new PutBucketRequest { BucketName = _options.Bucket, ObjectLockEnabledForBucket = true },
                    cancellationToken);
            }

            _bucketReady = true;
        }
        finally
        {
            _ensureBucket.Release();
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _ensureBucket.Dispose();
    }
}

/// <summary>Registered when no storage is configured: any use fails with a clear message instead of a null reference.</summary>
public sealed class UnconfiguredObjectStorage : IObjectStorage
{
    private static InvalidOperationException NotConfigured() =>
        new("Object storage is not configured. Set Storage:ServiceUrl, Storage:AccessKey and Storage:SecretKey.");

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => throw NotConfigured();

    public Task LockUntilAsync(string key, DateTimeOffset retainUntil, CancellationToken cancellationToken = default) =>
        throw NotConfigured();

    public Task<DateTimeOffset?> GetLockedUntilAsync(string key, CancellationToken cancellationToken = default) =>
        throw NotConfigured();
}
