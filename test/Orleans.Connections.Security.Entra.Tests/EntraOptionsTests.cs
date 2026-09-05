using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Connections.Security.Entra.Tests;

[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Security")]
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

    [Fact]
    public void Validate_AcceptsSeparateTokenScopeResourceApplicationIdAndClusterRole()
    {
        var options = EntraTestFixture.CreateOptions();
        options.TokenScope = "api://11111111-1111-1111-1111-111111111111/cluster-a";
        options.ResourceApplicationId = "44444444-4444-4444-4444-444444444444";
        options.ValidAudiences.Clear();
        options.ClusterClaimType = null;
        options.ClusterRole = "Orleans.Silo.Connect.cluster-a";

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_AcceptsExplicitClusterClaimBinding()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ResourceApplicationId = "44444444-4444-4444-4444-444444444444";
        options.ClusterRole = null;
        options.ClusterClaimType = "orleans_cluster";

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RejectsMissingResourceApplicationId()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ResourceApplicationId = null;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ResourceApplicationId), "must be configured");
    }

    [Theory]
    [InlineData("api://11111111-1111-1111-1111-111111111111")]
    [InlineData("not-an-application-id")]
    public void Validate_RejectsNonGuidResourceApplicationId(string resourceApplicationId)
    {
        var options = EntraTestFixture.CreateOptions();
        options.ResourceApplicationId = resourceApplicationId;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ResourceApplicationId), "must be a GUID");
    }

    [Fact]
    public void Validate_RejectsMissingClusterBinding()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ClusterClaimType = null;
        options.ClusterRole = null;

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ClusterRole), "A cluster role");
        Assert.Contains(nameof(options.ClusterClaimType), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsClusterAudienceAuthorization()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ClusterClaimType = null;
        options.ClusterRole = null;
#pragma warning disable CS0618
        options.ClusterAudienceFormat = "api://orleans-silos/{0}";
#pragma warning restore CS0618

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ClusterRole), "A cluster role");
        Assert.Contains(nameof(options.ClusterClaimType), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsAmbiguousRoleAndClaimBinding()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ClusterRole = "Orleans.Silo.Connect.cluster-a";
        options.ClusterClaimType = "orleans_cluster";

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ClusterRole), "either a cluster role");
        Assert.Contains(nameof(options.ClusterClaimType), result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsExactAndFormattedClusterRolesTogether()
    {
        var options = EntraTestFixture.CreateOptions();
        options.ClusterClaimType = null;
        options.ClusterRole = "Orleans.Silo.Connect.cluster-a";
        options.ClusterRoleFormat = "Orleans.Silo.Connect.{0}";

        var result = new EntraSiloConnectionOptionsValidator().Validate(Options.DefaultName, options);

        AssertValidationFailure(result, nameof(options.ClusterRole), "Only one");
        Assert.Contains(nameof(options.ClusterRoleFormat), result.FailureMessage, StringComparison.Ordinal);
    }

    private static void AssertValidationFailure(
        Microsoft.Extensions.Options.ValidateOptionsResult result,
        string memberName,
        string reason)
    {
        Assert.False(result.Succeeded);
        Assert.NotNull(result.Failures);
        var failure = Assert.Single(result.Failures);
        Assert.Contains(memberName, failure, StringComparison.Ordinal);
        Assert.Contains(reason, failure, StringComparison.Ordinal);
    }

}
