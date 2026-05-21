#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Placement.Repartitioning;
using Orleans.TestingHost;
using TestExtensions;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

[TestCategory("Functional"), TestCategory("ActivationRepartitioning")]
public class ActivationRepartitionerMigrationTests(ActivationRepartitionerMigrationTests.Fixture fixture) : IClassFixture<ActivationRepartitionerMigrationTests.Fixture>
{
    private readonly Fixture _fixture = fixture;

    private InProcessSiloHandle PrimarySilo => (InProcessSiloHandle)_fixture.HostedCluster.Primary;

    [Fact]
    public async Task FinalizeProtocol_DoesNotAwaitDeactivated_ForNonActivationDataContext()
    {
        var services = PrimarySilo.SiloHost.Services;
        var directory = services.GetRequiredService<ActivationDirectory>();
        var repartitioner = services.GetRequiredService<ActivationRepartitioner>();
        var context = new NonMigratingGrainContext(GrainId.Create("non-migrating", Guid.NewGuid().ToString()));

        directory.RecordNewTarget(context);
        try
        {
            await repartitioner.FinalizeProtocol([context.GrainId], [], SiloAddress.Zero, []).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(1, context.MigrateCallCount);
            Assert.False(context.Deactivated.IsCompleted);
        }
        finally
        {
            directory.RemoveTarget(context);
        }
    }

    public class Fixture : BaseTestClusterFixture
    {
        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.Options.InitialSilosCount = 1;
            builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        }

        private class SiloConfigurator : ISiloConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
#pragma warning disable ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                hostBuilder
                    .Configure<ActivationRepartitionerOptions>(options =>
                    {
                        options.MinRoundPeriod = TimeSpan.FromMinutes(5);
                        options.MaxRoundPeriod = TimeSpan.FromMinutes(6);
                    })
                    .AddActivationRepartitioner();
#pragma warning restore ORLEANSEXP001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
            }
        }
    }

    private sealed class NonMigratingGrainContext(GrainId grainId) : IGrainContext
    {
        private readonly TaskCompletionSource _deactivated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MigrateCallCount { get; private set; }
        public GrainId GrainId { get; } = grainId;
        public GrainReference GrainReference => throw new NotImplementedException();
        public object? GrainInstance => null;
        public ActivationId ActivationId { get; } = ActivationId.NewId();
        public GrainAddress Address => throw new NotImplementedException();
        public IServiceProvider ActivationServices => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler => throw new NotImplementedException();
        public Task Deactivated => _deactivated.Task;

        public void SetComponent<TComponent>(TComponent? value) where TComponent : class => throw new NotImplementedException();
        public void ReceiveMessage(object message) => throw new NotImplementedException();
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken = default) => MigrateCallCount++;
        public object? GetTarget() => null;
        public object? GetComponent(Type componentType) => null;
        public bool Equals(IGrainContext? other) => ReferenceEquals(this, other);
    }
}
