using Orleans.Configuration;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipOptionsTests
{
    [Fact]
    public void ProbeTimeoutBoundsTrackInitialTimeout()
    {
        var options = new ClusterMembershipOptions();

        Assert.Equal(TimeSpan.FromSeconds(5), options.ProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), options.MaxProbeTimeout);

        options.ProbeTimeout = TimeSpan.FromSeconds(8);

        Assert.Equal(TimeSpan.FromSeconds(4), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(32), options.MaxProbeTimeout);
    }

    [Fact]
    public void ExplicitProbeTimeoutBoundsDoNotTrackInitialTimeout()
    {
        var options = new ClusterMembershipOptions
        {
            MinProbeTimeout = TimeSpan.FromSeconds(2),
            MaxProbeTimeout = TimeSpan.FromSeconds(12),
        };

        options.ProbeTimeout = TimeSpan.FromSeconds(8);

        Assert.Equal(TimeSpan.FromSeconds(2), options.MinProbeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), options.MaxProbeTimeout);
    }

    [Fact]
    public void ProbeTimeoutIsNotObsolete()
    {
        var property = typeof(ClusterMembershipOptions).GetProperty("ProbeTimeout");

        Assert.NotNull(property);
        Assert.Empty(property.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: true));
    }
}
