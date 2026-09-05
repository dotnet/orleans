using Orleans.Runtime;
using TestExtensions;
using Xunit;

namespace Orleans.Streaming.Kinesis.Tests;

[TestSuite("BVT")]
[TestArea("Streaming")]
[TestProvider("Kinesis")]
[TestCategory("BVT"), TestCategory("Kinesis")]
public sealed class RecoveryCacheMemoryOptionsTests
{
    [Fact]
    public void Kinesis_EncodedCacheBudgetDefaultsTo64MiB()
    {
        var options = new KinesisStreamOptions();

        Assert.Equal(64L * 1024 * 1024, options.MaxCacheSizeBytes);
        new KinesisStreamOptionsValidator(options, "memory").ValidateConfiguration();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Kinesis_RejectsNonPositiveEncodedCacheBudget(long bytes)
    {
        var options = new KinesisStreamOptions { MaxCacheSizeBytes = bytes };

        var exception = Assert.Throws<OrleansConfigurationException>(
            () => new KinesisStreamOptionsValidator(options, "memory").ValidateConfiguration());

        Assert.Contains(nameof(KinesisStreamOptions.MaxCacheSizeBytes), exception.Message);
        Assert.Contains("memory", exception.Message);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(4L * 1024 * 1024 * 1024)]
    [InlineData(long.MaxValue)]
    public void Kinesis_AcceptsPositiveInt64EncodedCacheBudget(long bytes)
    {
        var options = new KinesisStreamOptions { MaxCacheSizeBytes = bytes };

        new KinesisStreamOptionsValidator(options, "memory").ValidateConfiguration();

        Assert.Equal(bytes, options.MaxCacheSizeBytes);
    }
}
