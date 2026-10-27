using System;
using System.Threading;
using System.Threading.Tasks;
using NSubstitute;
using Orleans.Configuration;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Xunit;

namespace UnitTests.Manifest;

/// <summary>
/// Exercises the fix for a rubber-duck/Copilot-review finding on dotnet/orleans#11438: a direct manifest fetch
/// that is still running when its attempt concludes (e.g. because peer repair filled every other missing silo
/// first) is not canceled, so it can complete after a newer attempt has already started. Before the fix,
/// <see cref="ClusterManifestProvider"/>'s completion handler applied such a completion unconditionally,
/// corrupting the newer attempt's diagnostics with state belonging to the superseded one.
/// </summary>
public partial class ClusterManifestProviderTests
{
    [Fact]
    public async Task MarkManifestFetchComplete_IgnoresCompletion_FromSupersededAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var localSilo = CreateSiloAddress(11111, 1);
        var peer = CreateSiloAddress(11112, 1);

        // First attempt: no missing silos, so it stamps and immediately concludes diagnostics for attempt 1.
        var emptySnapshot = CreateActiveMembershipSnapshot(1, localSilo, []);
        using var membership = new TestClusterMembershipService(emptySnapshot);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var options = new ClusterManifestOptions { EnableContentAddressedRetrieval = false };
        await using var provider = CreateClusterManifestProvider(localSilo, membership, grainFactory, options);
        await InitializeProviderAsync(provider, cancellationToken);
        Assert.True(await UpdateManifestAsync(provider, emptySnapshot, cancellationToken));

        var firstAttemptId = provider.LastAttemptDiagnostics!.AttemptId;

        // Second attempt: a peer is now missing, and its fetch never completes on its own (simulating a direct
        // fetch left running after peer repair supplied every missing silo for *this* attempt, so UpdateManifest
        // never awaits it). Run it without awaiting completion; it will remain pending.
        var secondSnapshot = CreateActiveMembershipSnapshot(2, localSilo, [peer]);
        var pendingFetch = new TaskCompletionSource<GrainManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = Substitute.For<ISiloManifestSystemTarget>();
        target.GetSiloManifest(Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<GrainManifest>(pendingFetch.Task));
        grainFactory.GetSystemTarget<ISiloManifestSystemTarget>(Constants.ManifestProviderType, peer).Returns(target);
        var secondUpdate = UpdateManifestAsync(provider, secondSnapshot, cancellationToken);

        var secondAttempt = provider.LastAttemptDiagnostics;
        Assert.NotNull(secondAttempt);
        Assert.NotEqual(firstAttemptId, secondAttempt!.AttemptId);
        Assert.Contains(peer, secondAttempt.PendingSilos);
        Assert.Null(secondAttempt.LastFailedSilo);

        // A completion carrying the first (now-superseded) attempt's id must not mutate the second attempt's
        // diagnostics, even though it targets the same silo the second attempt is genuinely waiting on.
        var staleFailure = new InvalidOperationException("stale fetch failure from a superseded attempt");
        MarkManifestFetchComplete(provider, firstAttemptId, peer, staleFailure);

        var afterStaleCompletion = provider.LastAttemptDiagnostics;
        Assert.NotNull(afterStaleCompletion);
        Assert.Equal(secondAttempt.AttemptId, afterStaleCompletion!.AttemptId);
        Assert.Contains(peer, afterStaleCompletion.PendingSilos);
        Assert.Null(afterStaleCompletion.LastFailedSilo);
        Assert.Null(afterStaleCompletion.LastFailureMessage);

        // The real completion, carrying the current attempt's id, is applied normally.
        pendingFetch.SetResult(CreateGrainManifest());
        Assert.True(await secondUpdate);
        var final = provider.LastAttemptDiagnostics;
        Assert.NotNull(final);
        Assert.Equal(secondAttempt.AttemptId, final!.AttemptId);
        Assert.DoesNotContain(peer, final.PendingSilos);
        Assert.Null(final.LastFailedSilo);
    }
}
