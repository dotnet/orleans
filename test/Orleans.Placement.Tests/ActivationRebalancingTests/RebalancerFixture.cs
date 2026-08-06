using TestExtensions;
using Orleans.Configuration;
using Orleans.TestingHost;
using Microsoft.Extensions.DependencyInjection;

namespace UnitTests.ActivationRebalancingTests;

public class RebalancerFixture : BaseInProcessTestClusterFixture
{
    public static readonly TimeSpan RebalancerDueTime = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SessionCyclePeriod = TimeSpan.FromSeconds(3);

    protected override void ConfigureTestCluster(InProcessTestClusterBuilder builder)
    {
        builder.Options.InitialSilosCount = 4;
        builder.Options.UseRealEnvironmentStatistics = true;
        builder.ConfigureSilo((options, siloBuilder)
#pragma warning disable ORLEANSEXP002
            => siloBuilder
                .Configure<SiloMessagingOptions>(o =>
                {
                    o.AssumeHomogenousSilosForTesting = true;
                    o.ClientGatewayShutdownNotificationTimeout = default;
                })
                .Configure<ActivationRebalancerOptions>(o =>
                {
                    o.RebalancerDueTime = RebalancerDueTime;
                    o.SessionCyclePeriod = SessionCyclePeriod;
                })
                .AddActivationRebalancer());
#pragma warning restore ORLEANSEXP002
    }
}
