using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra.Tests;

public sealed class EntraOptionsTests
{
    [Fact]
    public void SecureConfigurationIsValid()
    {
        var result = new EntraSiloConnectionOptionsValidator().Validate(
            Options.DefaultName,
            EntraTestFixture.CreateOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("http://login.microsoftonline.com/tenant/v2.0")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0")]
    [InlineData("https://login.microsoftonline.com/consumers/v2.0")]
    public void RejectsUntrustedOrTenantIndependentAuthority(string authority)
    {
        var options = EntraTestFixture.CreateOptions();
        options.Authority = new Uri(authority);

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void RequiresExplicitCallerAuthorization()
    {
        var options = EntraTestFixture.CreateOptions();
        options.AllowedClientIds.Clear();
        options.RequiredRoles.Clear();

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void RequiresExplicitClusterBinding()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ClusterClaimType = null;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsNonFiniteMetadataRefreshJitter(double jitter)
    {
        var options = EntraTestFixture.CreateOptions();
        options.MetadataRefreshJitterRatio = jitter;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void RejectsEffectivelyUnboundedMetadataWork()
    {
        var options = EntraTestFixture.CreateOptions();
        options.AutomaticMetadataRefreshInterval = TimeSpan.MaxValue;
        options.MaximumMetadataRefreshQueueSize = int.MaxValue;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
    }

}
