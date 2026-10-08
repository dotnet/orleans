using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using TestExtensions;
using Xunit;

namespace UnitTests.Manifest;

/// <summary>
/// Exercises <see cref="ClusterManifestProvider.LastAttemptDiagnostics"/>, the convergence-timeout diagnostic
/// snapshot added to diagnose dotnet/orleans#11434. These tests target the new diagnostic output/state-transition
/// contract directly: they do not exercise the pre-existing manifest-fetch success path on its own (already
/// covered elsewhere in this fixture), but specifically assert that pending-fetch state updates incrementally,
/// in real time, as individual fetches conclude -- rather than only reflecting a stale start-of-attempt snapshot.
/// </summary>
public partial class ClusterManifestProviderTests
{
    [Fact]
    public async Task LastAttemptDiagnostics_IsNull_BeforeFirstUpdateAttempt()
    {
        var localSilo = CreateSiloAddress(11111, 1);
        using var membership = new TestClusterMembershipService(CreateActiveMembershipSnapshot(1, localSilo, []));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory);

        Assert.Null(provider.LastAttemptDiagnostics);
    }

    [Fact]
    public async Task UpdateManifest_ClearsPendingSilos_WhenAllFetchesSucceed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [peer]);
        using var membership = new TestClusterMembershipService(snapshot);
        var grainFactory = CreateGrainFactory(peer, CreateGrainManifest());
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, options);
        await InitializeProviderAsync(provider, cancellationToken);

        Assert.True(await UpdateManifestAsync(provider, snapshot, cancellationToken));

        var diagnostics = provider.LastAttemptDiagnostics;
        Assert.NotNull(diagnostics);
        Assert.Equal(snapshot.Version, diagnostics!.MembershipVersion);
        Assert.Empty(diagnostics.PendingSilos);
        Assert.Null(diagnostics.LastFailedSilo);
        Assert.Null(diagnostics.LastFailureMessage);
        Assert.Null(diagnostics.LastFailureAt);
    }

    [Fact]
    public async Task UpdateManifest_TracksPendingFetchesInRealTime_AndRecordsFailureDiagnostics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var localSilo = CreateSiloAddress(11111, 1);
        var slowPeer = CreateSiloAddress(11112, 1);
        var failingPeer = CreateSiloAddress(11113, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [slowPeer, failingPeer]);
        using var membership = new TestClusterMembershipService(snapshot);

        var slowFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowTarget = Substitute.For<ISiloManifestSystemTarget>();
        slowTarget.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<GrainManifest>(slowFetch.Task));

        var failure = new InvalidOperationException("simulated fetch failure");
        var failingTarget = Substitute.For<ISiloManifestSystemTarget>();
        failingTarget.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException<GrainManifest>(failure));

        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, slowPeer).Returns(slowTarget);
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, failingPeer).Returns(failingTarget);

        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(
            localSilo, membership, grainFactory, timeProvider, NullLogger<ClusterManifestProvider>.Instance, options: options);
        await InitializeProviderAsync(provider, cancellationToken);

        var attemptStartedAt = timeProvider.GetUtcNow().UtcDateTime;
        var update = UpdateManifestAsync(provider, snapshot, cancellationToken);

        // The attempt-start snapshot is stamped before fetches are dispatched. The failing peer's fetch may
        // already have completed and been removed by the time this is observed (its failure is effectively
        // synchronous), so only the still-outstanding slow peer is guaranteed to remain visible here.
        var started = provider.LastAttemptDiagnostics;
        Assert.NotNull(started);
        Assert.Equal(snapshot.Version, started!.MembershipVersion);
        Assert.Equal(attemptStartedAt, started.AttemptStartedAt);
        Assert.Contains(slowPeer, started.PendingSilos);

        // Poll for the failing peer's fetch to conclude. This specifically verifies the fix for the rubber-duck
        // finding that pending state was previously only updated once per whole attempt (after Task.WhenAll),
        // rather than incrementally as each individual fetch concludes.
        ManifestUpdateAttemptDiagnostics? diagnostics = null;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            diagnostics = provider.LastAttemptDiagnostics;
            if (diagnostics is { LastFailedSilo: not null })
            {
                break;
            }

            await Task.Delay(10, cancellationToken);
        }

        Assert.NotNull(diagnostics);
        Assert.Equal(failingPeer, diagnostics!.LastFailedSilo);
        Assert.Equal(failure.Message, diagnostics.LastFailureMessage);
        Assert.NotNull(diagnostics.LastFailureAt);

        // The failing peer's fetch concluded (removed from pending), while the still-outstanding slow peer's
        // fetch has not: pending state reflects each fetch's own completion, not the whole attempt's.
        Assert.DoesNotContain(failingPeer, diagnostics.PendingSilos);
        Assert.Contains(slowPeer, diagnostics.PendingSilos);

        // Completing the slow peer's fetch removes it from pending too, even though the attempt as a whole
        // still fails overall (because the failing peer never produced a manifest for this attempt).
        slowFetch.SetResult(CreateGrainManifest());
        Assert.False(await update);
        var final = provider.LastAttemptDiagnostics;
        Assert.NotNull(final);
        Assert.Empty(final!.PendingSilos);
        Assert.Equal(failingPeer, final.LastFailedSilo);
    }
}
