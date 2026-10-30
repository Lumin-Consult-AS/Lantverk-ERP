using Testcontainers.Minio;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>
/// A real S3-compatible object store for the tests that verify the S3 adapter (bucket creation with Object Lock,
/// compliance-mode retention). It starts only when <c>ERP_TEST_S3_IMAGE</c> names a MinIO-compatible image: the
/// official <c>minio/minio</c> image is no longer published on Docker Hub, so the image is a deliberate choice of
/// whoever runs the tests. Without it these tests are skipped.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    public const string ImageVariable = "ERP_TEST_S3_IMAGE";
    public const string AccessKey = "erp-test-access";
    public const string SecretKey = "erp-test-secret-key";

    private MinioContainer? _container;

    public static bool IsEnabled => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ImageVariable));

    public string ServiceUrl => _container?.GetConnectionString()
        ?? throw new InvalidOperationException($"Set {ImageVariable} to run the S3 integration tests.");

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            return;
        }

        _container = new MinioBuilder(Environment.GetEnvironmentVariable(ImageVariable)!)
            .WithUsername(AccessKey)
            .WithPassword(SecretKey)
            .Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>A fact that is skipped unless a MinIO-compatible image has been chosen (see <see cref="MinioFixture"/>).</summary>
public sealed class RequiresS3FactAttribute : FactAttribute
{
    public RequiresS3FactAttribute()
    {
        if (!MinioFixture.IsEnabled)
        {
            Skip = $"Set {MinioFixture.ImageVariable} to a MinIO-compatible image to run the S3 integration tests.";
        }
    }
}
