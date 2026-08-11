using System.Reflection;
using Microsoft.Extensions.Configuration;
using Orleans.Configuration;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipOptionsTests
{
    [Fact]
    public void ProbeTimeoutDefaultsTrackInitialTimeout()
    {
        var options = new ClusterMembershipOptions();

        Assert.Equal(TimeSpan.FromSeconds(5), options.ProbeInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), options.InitialProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.MaxProbeTimeout);

        options.InitialProbeTimeout = TimeSpan.FromSeconds(8);

        Assert.Equal(TimeSpan.FromSeconds(4), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(16), options.MaxProbeTimeout);
    }

    [Fact]
    public void ExplicitProbeTimeoutBoundsDoNotTrackInitialTimeout()
    {
        var options = new ClusterMembershipOptions
        {
            MinProbeTimeout = TimeSpan.FromSeconds(2),
            MaxProbeTimeout = TimeSpan.FromSeconds(12),
        };

        options.InitialProbeTimeout = TimeSpan.FromSeconds(8);

        Assert.Equal(TimeSpan.FromSeconds(2), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), options.MaxProbeTimeout);
    }

    [Fact]
    public void ObsoleteProbeTimeoutSetsIntervalAndInitialTimeout()
    {
        var options = new ClusterMembershipOptions();
        var property = typeof(ClusterMembershipOptions).GetProperty("ProbeTimeout");

        property!.SetValue(options, TimeSpan.FromSeconds(7));

        Assert.Equal(TimeSpan.FromSeconds(7), options.ProbeInterval);
        Assert.Equal(TimeSpan.FromSeconds(7), options.InitialProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7), property.GetValue(options));
        Assert.False(property.GetCustomAttribute<ObsoleteAttribute>()!.IsError);
    }

    [Fact]
    public void NewProbeOptionsOverrideLegacyConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProbeTimeout"] = "00:00:05",
                ["ProbeInterval"] = "00:00:01",
                ["InitialProbeTimeout"] = "00:00:02",
            })
            .Build();

        var options = configuration.Get<ClusterMembershipOptions>();

        Assert.Equal(TimeSpan.FromSeconds(1), options!.ProbeInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), options.InitialProbeTimeout);
    }
}
