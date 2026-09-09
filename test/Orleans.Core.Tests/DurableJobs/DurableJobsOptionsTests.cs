using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
public class DurableJobsOptionsTests
{
    [Fact]
    public void DiscoveryTimingDefaults()
    {
        var options = new DurableJobsOptions();

        Assert.Equal(TimeSpan.FromMinutes(10), options.ShardLoadLookaheadPeriod);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ShardCheckInterval);
        CreateValidator(options).ValidateConfiguration();
    }

    [Fact]
    public void ValidateConfiguration_NegativeLookahead_Throws()
    {
        var options = new DurableJobsOptions { ShardLoadLookaheadPeriod = TimeSpan.FromTicks(-1) };

        var exception = Assert.Throws<OrleansConfigurationException>(CreateValidator(options).ValidateConfiguration);

        Assert.Contains(nameof(DurableJobsOptions.ShardLoadLookaheadPeriod), exception.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0.9999)]
    [InlineData(4294967295)]
    public void ValidateConfiguration_InvalidCheckInterval_Throws(double milliseconds)
    {
        var options = new DurableJobsOptions { ShardCheckInterval = TimeSpan.FromMilliseconds(milliseconds) };

        var exception = Assert.Throws<OrleansConfigurationException>(CreateValidator(options).ValidateConfiguration);

        Assert.Contains(nameof(DurableJobsOptions.ShardCheckInterval), exception.Message);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(23, 120000)]
    [InlineData(10, 4294967294)]
    public void ValidateConfiguration_ValidDiscoveryTimings_AcceptsValues(int lookaheadMinutes, double intervalMilliseconds)
    {
        var options = new DurableJobsOptions
        {
            ShardLoadLookaheadPeriod = TimeSpan.FromMinutes(lookaheadMinutes),
            ShardCheckInterval = TimeSpan.FromMilliseconds(intervalMilliseconds)
        };

        CreateValidator(options).ValidateConfiguration();
        using var timer = new PeriodicTimer(options.ShardCheckInterval);
        Assert.Equal(options.ShardCheckInterval, timer.Period);
        Assert.Equal(TimeSpan.FromMinutes(lookaheadMinutes), options.ShardLoadLookaheadPeriod);
    }

    private static DurableJobsOptionsValidator CreateValidator(DurableJobsOptions options) =>
        new(NullLogger<DurableJobsOptionsValidator>.Instance, Options.Create(options));
}
