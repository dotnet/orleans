using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Dashboard;
using Orleans.Hosting;
using Xunit;

namespace UnitTests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Dashboard")]
public class DashboardOptionsTests
{
    [Fact]
    public void DashboardOptions_Defaults_AreOneSecondOneHundredAndTraceVisible()
    {
        var options = new DashboardOptions();

        Assert.Equal(1_000, options.CounterUpdateIntervalMs);
        Assert.Equal(100, options.HistoryLength);
        Assert.False(options.HideTrace);
    }

    [Fact]
    public void AddDashboard_ConfigureOptions_PreservesConfiguredIntervalHistoryLengthAndHideTrace()
    {
        var builder = new TestClientBuilder();

        builder.AddDashboard(options =>
        {
            options.CounterUpdateIntervalMs = 2_750;
            options.HistoryLength = 37;
            options.HideTrace = true;
        });

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.Equal(2_750, options.CounterUpdateIntervalMs);
        Assert.Equal(37, options.HistoryLength);
        Assert.True(options.HideTrace);
    }

    [Fact]
    public void AddDashboard_SiloBuilderConfigureOptions_PreservesConfiguredIntervalHistoryLengthAndHideTrace()
    {
        var builder = new TestSiloBuilder();

        builder.AddDashboard(options =>
        {
            options.CounterUpdateIntervalMs = 3_250;
            options.HistoryLength = 48;
            options.HideTrace = true;
        });

        using var provider = builder.Services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.Equal(3_250, options.CounterUpdateIntervalMs);
        Assert.Equal(48, options.HistoryLength);
        Assert.True(options.HideTrace);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; } =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
    }

    private sealed class TestClientBuilder : IClientBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public Microsoft.Extensions.Configuration.IConfiguration Configuration { get; } =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
    }
}
