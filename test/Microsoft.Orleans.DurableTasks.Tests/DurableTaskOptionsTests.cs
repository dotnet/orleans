using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Hosting;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.Tests;

/// <summary>Verifies the validation of <see cref="DurableTaskOptions"/>.</summary>
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableTasks")]
public sealed class DurableTaskOptionsTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        var options = Resolve(static _ => { });

        Assert.Equal(TimeSpan.FromDays(1), options.ResultRetentionPeriod);
        Assert.Equal(TimeSpan.FromMinutes(1), options.StalledExecutionRetryInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void StalledExecutionRetryIntervalIsPositive(int seconds)
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(options => options.StalledExecutionRetryInterval = TimeSpan.FromSeconds(seconds)));

        Assert.Contains("stalled execution retry interval must be positive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultRetentionPeriodIsNotNegative()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Resolve(options => options.ResultRetentionPeriod = TimeSpan.FromSeconds(-1)));

        Assert.Contains("result retention must not be negative", exception.Message, StringComparison.Ordinal);
    }

    private static DurableTaskOptions Resolve(Action<DurableTaskOptions> configure)
    {
        var silo = new OptionsSiloBuilder();
        silo.AddDurableTasks(configure);
        using var services = silo.Services.BuildServiceProvider();
        return services.GetRequiredService<IOptions<DurableTaskOptions>>().Value;
    }

    private sealed class OptionsSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}
