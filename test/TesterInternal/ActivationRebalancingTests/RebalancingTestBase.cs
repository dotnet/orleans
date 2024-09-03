using TestExtensions;
using Xunit;
using CsCheck;
using Xunit.Abstractions;
using Orleans.Configuration;
using Orleans.TestingHost;

namespace UnitTests.ActivationRebalancingTests;

public abstract class RebalancingTestBase : BaseTestClusterFixture
{
    protected SiloAddress Silo1 { get; }
    protected SiloAddress Silo2 { get; }
    protected SiloAddress Silo3 { get; }
    protected SiloAddress Silo4 { get; }

    protected ITestOutputHelper OutputHelper { get; }
    protected new IGrainFactory GrainFactory { get; }
    protected IManagementGrain MgmtGrain { get; }

    protected RebalancingTestBase(Fixture fixture, ITestOutputHelper output)
    {
        var silos = fixture.HostedCluster.GetActiveSilos().Select(h => h.SiloAddress).OrderBy(s => s).ToArray();

        Silo1 = silos[0];
        Silo2 = silos[1];
        Silo3 = silos[2];
        Silo4 = silos[3];

        OutputHelper = output;
        GrainFactory = fixture.HostedCluster.GrainFactory;
        MgmtGrain = GrainFactory.GetGrain<IManagementGrain>(0);
    }

    protected static ulong GetActivationCount(DetailedGrainStatistic[] stats, SiloAddress silo) =>
        (ulong)stats.Count(x => x.SiloAddress.Equals(silo));

    public override async Task InitializeAsync()
    {
        await GrainFactory
            .GetGrain<IManagementGrain>(0)
            .ForceActivationCollection(TimeSpan.Zero);

        await base.InitializeAsync();
    }

    public class Fixture : BaseTestClusterFixture
    {
        public static readonly TimeSpan RebalancerDueTime = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan SessionCyclePeriod = TimeSpan.FromSeconds(5);

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 4;
            builder.Options.UseRealEnvironmentStatistics = true;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
#pragma warning disable ORLEANSEXP002
                => hostBuilder
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
                    .AddActivationRebalancer();
#pragma warning restore ORLEANSEXP002
        }
    }
}

public interface IRebalancingTestGrain : IGrainWithGuidKey
{
    Task Ping();
}

public class RebalancingTestGrain : Grain, IRebalancingTestGrain
{
    public Task Ping() => Task.CompletedTask;
}