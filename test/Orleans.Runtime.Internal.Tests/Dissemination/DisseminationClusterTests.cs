#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
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
    public async Task MembershipUpdatesAreDisseminatedAndAppliedAcrossRealCluster()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        // Observe membership updates that were applied on remote silos; require at least two distinct silos
        // to prove that updates propagated across the cluster rather than only being applied locally.
        var observer = new ValueApplyObserver(targetDistinctSilos: 2);
        using var subscription = DisseminationEvents.Listener.Subscribe(
            observer,
            static name => name == "Dissemination.ValueApply");

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

        // Adding a silo produces new membership updates that must propagate to the existing silos.
        await cluster.StartAdditionalSiloAsync();
        await cluster.WaitForLivenessToStabilizeAsync();

        await observer.Reached.WaitAsync(cancellation.Token);

        Assert.True(
            observer.AppliedSilos.Count >= 2,
            "Expected membership updates to be applied on at least 2 distinct silos, but saw: "
                + string.Join(", ", observer.AppliedSilos.Select(static silo => silo.ToString())));
    }

    private sealed class ValueApplyObserver(int targetDistinctSilos) : IObserver<KeyValuePair<string, object?>>
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
                || evt.Result != DisseminationApplyResult.Applied.ToString()
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
