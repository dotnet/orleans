#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.Dissemination;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DisseminationDiagnosticCollection
{
    public const string Name = "Dissemination diagnostics";
}

/// <summary>
/// Exercises dissemination across a real in-process cluster so that membership updates flow through the
/// full serialization pipeline (the in-memory transport is a byte pipe, not object pass-through). This
/// guards against wire types that lack serialization codecs, which unit tests using a fake transport
/// cannot catch.
/// </summary>
[TestCategory("Functional"), TestCategory("Dissemination")]
[TestSuite("Functional")]
[TestProvider("None")]
[TestArea("Dissemination")]
[Collection(DisseminationDiagnosticCollection.Name)]
public sealed class DisseminationClusterTests
{
    [Fact]
    public async Task MembershipUpdatesAreDisseminatedAcrossRealClusterWithExplicitOptIn()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(120));
        var cancellationToken = cancellation.Token;

        var builder = new InProcessTestClusterBuilder(3);
        builder.ConfigureSilo((_, silo) => new EnabledDisseminationConfigurator().Configure(silo));
        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken);

        var existingSilos = cluster.GetActiveSilos().Select(static silo => silo.SiloAddress).ToHashSet();
        var baselineVersion = cluster.Silos[0].ServiceProvider
            .GetRequiredService<IMembershipManager>()
            .CurrentSnapshot.Version.Value;

        // Arm the observer after initial stabilization and accept only a later membership version applied by
        // pre-existing silos, so cluster startup or unrelated parallel tests cannot satisfy the assertion.
        var observer = new ValueApplyObserver(targetDistinctSilos: 2, existingSilos, baselineVersion);
        using var subscription = DisseminationEvents.Listener.Subscribe(
            observer,
            static name => name == "Dissemination.ValueApply");

        // Adding a silo produces new membership updates that must propagate to the existing silos.
        await cluster.StartAdditionalSiloAsync().WaitAsync(cancellationToken);
        await cluster.WaitForLivenessToStabilizeAsync().WaitAsync(cancellationToken);

        await observer.Reached.WaitAsync(cancellationToken);

        Assert.True(
            observer.AppliedSilos.Count >= 2,
            "Expected membership updates to be applied on at least 2 distinct silos, but saw: "
                + string.Join(", ", observer.AppliedSilos.Select(static silo => silo.ToString())));
    }

    [Fact]
    public async Task DevelopmentPrimaryRestartRejectsRetainedPreResetMembership()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMinutes(3));
        var cancellationToken = cancellation.Token;
        var builder = new TestClusterBuilder(3);
        builder.Options.InitializeClientOnDeploy = false;
        builder.AddSiloBuilderConfigurator<EnabledDisseminationConfigurator>();
        await using var cluster = builder.Build();
        await cluster.DeployAsync(cancellationToken);
        var primary = Assert.IsType<InProcessSiloHandle>(cluster.Primary);
        Assert.IsType<SystemTargetBasedMembershipTable>(primary.SiloHost.Services.GetRequiredService<IMembershipTable>());
        var retained = primary.SiloHost.Services.GetRequiredService<IMembershipManager>().CurrentSnapshot;
        var originalSilos = cluster.GetActiveSilos().ToArray();

        var restarted = Assert.IsType<InProcessSiloHandle>(
            await cluster.RestartSiloAsync(primary).WaitAsync(cancellationToken));
        Assert.NotEqual(primary.SiloAddress, restarted.SiloAddress);
        var manager = restarted.SiloHost.Services.GetRequiredService<IMembershipManager>();
        Assert.True(manager.CurrentSnapshot.Version < retained.Version);
        await manager.ProcessGossipSnapshot(retained, cancellationToken);
        Assert.Equal(SiloStatus.Active, manager.LocalSiloStatus);
        Assert.Contains(restarted.SiloAddress, manager.CurrentSnapshot.Entries.Keys);
        Assert.DoesNotContain(primary.SiloAddress, manager.CurrentSnapshot.Entries.Keys);

        foreach (var silo in originalSilos.Where(silo => !ReferenceEquals(silo, primary)))
        {
            await cluster.RestartSiloAsync(silo).WaitAsync(cancellationToken);
        }

        foreach (var silo in cluster.GetActiveSilos().Cast<InProcessSiloHandle>())
        {
            var current = silo.SiloHost.Services.GetRequiredService<IMembershipManager>();
            await current.Refresh(null, cancellationToken, requireFresh: true);
            Assert.Equal(SiloStatus.Active, current.LocalSiloStatus);
            Assert.Contains(restarted.SiloAddress, current.CurrentSnapshot.Entries.Keys);
            Assert.DoesNotContain(primary.SiloAddress, current.CurrentSnapshot.Entries.Keys);
        }
    }

    public sealed class EnabledDisseminationConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder builder) => builder.ConfigureServices(services =>
        {
            services.Configure<DisseminationOptions>(options => options.Enabled = true);
            services.Configure<ClusterMembershipOptions>(options => options.Dissemination.Enabled = true);
            services.Configure<DeploymentLoadPublisherOptions>(options => options.Dissemination.Enabled = true);
        });
    }

    private sealed class ValueApplyObserver(
        int targetDistinctSilos,
        IReadOnlySet<SiloAddress> expectedSilos,
        long baselineVersion) : IObserver<KeyValuePair<string, object?>>
    {
        private readonly object _lock = new();
        private readonly HashSet<SiloAddress> _appliedSilos = [];
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Reached => _reached.Task;

        public IReadOnlyCollection<SiloAddress> AppliedSilos
        {
            get
            {
                lock (_lock)
                {
                    return _appliedSilos.ToArray();
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is not DisseminationValueEvent evt
                || evt.Namespace != DisseminationNamespaceNames.Membership
                || evt.Key != DisseminationKey.Default
                || evt.ToVersion <= baselineVersion
                || !expectedSilos.Contains(evt.LocalSilo)
                || evt.Result is not (nameof(DisseminationApplyResult.Applied) or nameof(DisseminationApplyResult.Duplicate))
                || evt.PayloadBytes <= 0)
            {
                return;
            }

            lock (_lock)
            {
                _appliedSilos.Add(evt.LocalSilo);
                if (_appliedSilos.Count >= targetDistinctSilos)
                {
                    _reached.TrySetResult();
                }
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }
    }
}
