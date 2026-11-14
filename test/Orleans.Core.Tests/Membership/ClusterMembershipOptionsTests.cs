using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Xunit;

namespace NonSilo.Tests.Membership;

[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipOptionsTests
{
    [Fact]
    public void SiloGenerationRetirementAge_DefaultsToOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), new ClusterMembershipOptions().SiloGenerationRetirementAge);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(36_000_000_000L)]
    public void SiloGenerationRetirementAge_ValidatorAcceptsNullZeroAndPositiveValues(long? ticks)
    {
        using var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IMembershipTable>())
            .Configure<ClusterMembershipOptions>(options =>
                options.SiloGenerationRetirementAge = ticks is { } value ? TimeSpan.FromTicks(value) : null)
            .BuildServiceProvider();

        new SiloClusteringValidator(services).ValidateConfiguration();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(-36_000_000_000L)]
    public void SiloGenerationRetirementAge_ValidatorRejectsNegativeValues(long ticks)
    {
        using var services = new ServiceCollection()
            .AddSingleton(Substitute.For<IMembershipTable>())
            .Configure<ClusterMembershipOptions>(options => options.SiloGenerationRetirementAge = TimeSpan.FromTicks(ticks))
            .BuildServiceProvider();

        var exception = Assert.Throws<OrleansConfigurationException>(() => new SiloClusteringValidator(services).ValidateConfiguration());

        Assert.Contains("ClusterMembershipOptions.SiloGenerationRetirementAge", exception.Message);
        Assert.Contains("must be greater than or equal to 0, or null.", exception.Message);
    }

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
