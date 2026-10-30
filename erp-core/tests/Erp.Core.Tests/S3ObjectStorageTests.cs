using System.Text;
using Erp.Core.Infrastructure.Storage;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>
/// Verifies the S3 adapter against a real S3-compatible server: bucket creation with Object Lock and compliance-mode
/// retention. Skipped unless <c>ERP_TEST_S3_IMAGE</c> names a MinIO-compatible image (see <see cref="MinioFixture"/>).
/// </summary>
public sealed class S3ObjectStorageTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private S3ObjectStorage CreateStorage() => new(new ObjectStorageOptions
    {
        ServiceUrl = minio.ServiceUrl,
        AccessKey = MinioFixture.AccessKey,
        SecretKey = MinioFixture.SecretKey,
        Bucket = $"erp-test-{Guid.NewGuid():N}"[..30],
        ForcePathStyle = true,
        CreateBucketIfMissing = true
    });

    [RequiresS3Fact]
    public async Task StoresAndReadsContent_AndTreatsMissingKeysAsNull()
    {
        using var storage = CreateStorage();
        var key = $"tenants/t/files/{Guid.NewGuid():N}";

        await storage.PutAsync(key, Encoding.UTF8.GetBytes("innhold"), "text/plain");

        Assert.Equal("innhold", Encoding.UTF8.GetString((await storage.GetAsync(key))!));
        Assert.Null(await storage.GetAsync("finnes-ikke"));
        await storage.DeleteAsync(key);
        Assert.Null(await storage.GetAsync(key));
    }

    [RequiresS3Fact]
    public async Task ComplianceRetention_BlocksDeletion_CanOnlyBeExtended_AndSurvivesDeleteMarkers()
    {
        using var storage = CreateStorage();
        var key = $"tenants/t/files/{Guid.NewGuid():N}";
        await storage.PutAsync(key, Encoding.UTF8.GetBytes("bilag"), "application/pdf");

        var until = DateTimeOffset.UtcNow.AddYears(5);
        await storage.LockUntilAsync(key, until);
        var locked = await storage.GetLockedUntilAsync(key);
        Assert.NotNull(locked);
        Assert.InRange(locked.Value, until.AddSeconds(-5), until.AddSeconds(5));

        await Assert.ThrowsAsync<ObjectLockedException>(() => storage.DeleteAsync(key));
        Assert.Equal("bilag", Encoding.UTF8.GetString((await storage.GetAsync(key))!));

        // A shorter period is ignored; a longer one extends the lock
        await storage.LockUntilAsync(key, DateTimeOffset.UtcNow.AddYears(1));
        Assert.InRange((await storage.GetLockedUntilAsync(key))!.Value, until.AddSeconds(-5), until.AddSeconds(5));
        await storage.LockUntilAsync(key, until.AddYears(2));
        Assert.True((await storage.GetLockedUntilAsync(key))! > until.AddYears(1));
    }
}
