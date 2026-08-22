#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Orleans.Runtime.MembershipService;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.Dissemination;

/// <summary>
/// Exercises dissemination across a real in-process cluster so that membership updates flow through the
/// full serialization pipeline (the in-memory transport is a byte pipe, not object pass-through). This
/// guards against wire types that lack serialization codecs, which unit tests using a fake transport
/// cannot catch.
/// </summary>
[TestCategory("Functional"), TestCategory("Dissemination")]
public sealed class DisseminationClusterTests
{
    [Fact]
    public async Task MembershipUpdatesAreDisseminatedAcrossRealCluster()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var builder = new InProcessTestClusterBuilder(3);
        builder.ConfigureSilo((_, siloBuilder) =>
        {
            // Dissemination and the membership namespace are disabled by default; enable both.
            siloBuilder.Configure<DisseminationOptions>(options => options.Enabled = true);
            siloBuilder.Configure<ClusterMembershipOptions>(options => options.Dissemination.Enabled = true);
        });

        await using var cluster = builder.Build();
        await cluster.DeployAsync();
        await cluster.WaitForLivenessToStabilizeAsync();

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
        await cluster.StartAdditionalSiloAsync();
        await cluster.WaitForLivenessToStabilizeAsync();

        await observer.Reached.WaitAsync(cancellation.Token);

        Assert.True(
            observer.AppliedSilos.Count >= 2,
            "Expected membership updates to be applied on at least 2 distinct silos, but saw: "
                + string.Join(", ", observer.AppliedSilos.Select(static silo => silo.ToString())));
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
