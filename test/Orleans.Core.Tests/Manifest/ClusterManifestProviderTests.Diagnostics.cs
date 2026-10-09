using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
/// Covers manifest-update diagnostic snapshots and their concurrent state transitions.
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

    [Fact]
    public async Task DiagnosticsSnapshot_RetriesAfterConcurrentAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        var initialSnapshot = CreateActiveMembershipSnapshot(1, localSilo, []);
        var nextSnapshot = CreateActiveMembershipSnapshot(2, localSilo, [peer]);
        var membership = Substitute.For<IClusterMembershipService>();
        membership.CurrentSnapshot.Returns(initialSnapshot);
        var grainFactory = CreateGrainFactory(peer, CreateGrainManifest());
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, options);
        await InitializeProviderAsync(provider, cancellationToken);
        Assert.True(await UpdateManifestAsync(provider, initialSnapshot, cancellationToken));
        var initialDiagnostics = provider.LastAttemptDiagnostics;

        Task<bool>? concurrentUpdate = null;
        var installAttempt = true;
        membership.CurrentSnapshot.Returns(_ =>
        {
            // Advance the attempt and publication between the snapshot's diagnostic and manifest reads.
            if (installAttempt)
            {
                installAttempt = false;
                concurrentUpdate = UpdateManifestAsync(provider, nextSnapshot, cancellationToken);
            }

            return nextSnapshot;
        });

        var (manifest, diagnostics) = provider.GetManifestAndDiagnosticsSnapshot();

        Assert.NotNull(concurrentUpdate);
        Assert.True(await concurrentUpdate);
        Assert.Same(provider.LastAttemptDiagnostics, diagnostics);
        Assert.NotSame(initialDiagnostics, diagnostics);
        Assert.Equal(nextSnapshot.Version, diagnostics!.MembershipVersion);
        Assert.Empty(diagnostics.PendingSilos);
        Assert.Equal(new MajorMinorVersion(2, 1), manifest.Version);
        Assert.Contains(peer, manifest.Silos.Keys);
    }

    [Fact]
    public async Task DiagnosticsSnapshot_ReflectsMembershipBeforeNextAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        var initialSnapshot = CreateActiveMembershipSnapshot(1, localSilo, [peer]);
        using var membership = new TestClusterMembershipService(initialSnapshot);
        var grainFactory = CreateGrainFactory(peer, CreateGrainManifest());
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, options);
        await InitializeProviderAsync(provider, cancellationToken);
        Assert.True(await UpdateManifestAsync(provider, initialSnapshot, cancellationToken));
        var previousAttempt = provider.LastAttemptDiagnostics;

        membership.Update(CreateActiveMembershipSnapshot(2, localSilo, []));
        var (manifest, diagnostics) = provider.GetManifestAndDiagnosticsSnapshot();

        Assert.Equal(new MajorMinorVersion(2, 0), manifest.Version);
        Assert.Equal(localSilo, Assert.Single(manifest.Silos).Key);
        Assert.Same(previousAttempt, diagnostics);
        Assert.Equal(initialSnapshot.Version, diagnostics!.MembershipVersion);
        Assert.Empty(diagnostics.PendingSilos);
    }

    [Fact]
    public async Task UpdateManifest_PreservesFailureDuringAttemptInstallation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        var initialSnapshot = CreateActiveMembershipSnapshot(1, localSilo, []);
        using var membership = new TestClusterMembershipService(initialSnapshot);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var timeProvider = Substitute.For<TimeProvider>();
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        timeProvider.GetUtcNow().Returns(now);
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(
            localSilo, membership, grainFactory, timeProvider, NullLogger<ClusterManifestProvider>.Instance, options: options);
        await InitializeProviderAsync(provider, cancellationToken);
        Assert.True(await UpdateManifestAsync(provider, initialSnapshot, cancellationToken));
        var firstAttemptId = provider.LastAttemptDiagnostics!.AttemptId;

        var failure = new InvalidOperationException("Fetch failed during attempt installation");
        var completePreviousFetch = true;
        timeProvider.GetUtcNow().Returns(_ =>
        {
            // Complete the old fetch after its diagnostics were read, while the new record is constructed.
            if (completePreviousFetch)
            {
                completePreviousFetch = false;
                MarkManifestFetchComplete(provider, firstAttemptId, peer, failure);
            }

            return now;
        });

        var nextSnapshot = CreateActiveMembershipSnapshot(2, localSilo, [peer]);
        var pendingFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Substitute.For<ISiloManifestSystemTarget>();
        target.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<GrainManifest>(pendingFetch.Task));
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(target);
        var update = UpdateManifestAsync(provider, nextSnapshot, cancellationToken);
        try
        {
            var diagnostics = provider.LastAttemptDiagnostics;
            Assert.NotNull(diagnostics);
            Assert.True(diagnostics.AttemptId > firstAttemptId);
            Assert.Equal(nextSnapshot.Version, diagnostics.MembershipVersion);
            Assert.Equal(peer, Assert.Single(diagnostics.PendingSilos));
            Assert.Equal(peer, diagnostics.LastFailedSilo);
            Assert.Equal(failure.Message, diagnostics.LastFailureMessage);
            Assert.Equal(now.UtcDateTime, diagnostics.LastFailureAt);
        }
        finally
        {
            pendingFetch.TrySetResult(CreateGrainManifest());
            Assert.True(await update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        Assert.Empty(provider.LastAttemptDiagnostics!.PendingSilos);
        Assert.Equal(failure.Message, provider.LastAttemptDiagnostics.LastFailureMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateManifest_ClassifiesCancellationByRequestToken(bool cancelRequest)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);
        var snapshot = CreateActiveMembershipSnapshot(1, localSilo, [peer]);
        using var membership = new TestClusterMembershipService(snapshot);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pendingFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Substitute.For<ISiloManifestSystemTarget>();
        target.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<GrainManifest>(pendingFetch.Task));
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(target);
        var timeProvider = new FakeTimeProvider();
        var logger = Substitute.For<ILogger<ClusterManifestProvider>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(
            localSilo, membership, grainFactory, timeProvider, logger, options: options);
        await InitializeProviderAsync(provider, cancellationToken);
        var update = UpdateManifestAsync(provider, snapshot, requestCancellation.Token);
        var failure = new OperationCanceledException("Peer canceled the manifest request", new CancellationToken(canceled: true));
        try
        {
            Assert.Equal(peer, Assert.Single(provider.LastAttemptDiagnostics!.PendingSilos));
            if (cancelRequest)
            {
                requestCancellation.Cancel();
            }

            pendingFetch.SetException(failure);
            if (cancelRequest)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }
            else
            {
                Assert.False(await update.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }

            var diagnostics = provider.LastAttemptDiagnostics!;
            Assert.Empty(diagnostics.PendingSilos);
            var warnings = logger.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log) && Equals(call.GetArguments()[0], LogLevel.Warning))
                .ToArray();
            if (cancelRequest)
            {
                Assert.Null(diagnostics.LastFailedSilo);
                Assert.Null(diagnostics.LastFailureMessage);
                Assert.Null(diagnostics.LastFailureAt);
                Assert.Empty(warnings);
            }
            else
            {
                Assert.Equal(peer, diagnostics.LastFailedSilo);
                Assert.Equal(failure.Message, diagnostics.LastFailureMessage);
                Assert.Equal(timeProvider.GetUtcNow().UtcDateTime, diagnostics.LastFailureAt);
                Assert.Same(failure, Assert.Single(warnings).GetArguments()[3]);
            }
        }
        finally
        {
            requestCancellation.Cancel();
            pendingFetch.TrySetResult(CreateGrainManifest());
        }
    }
}
