using System;
using System.Collections.Immutable;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Runtime.Metadata;
using Orleans.TestingHost;
using Xunit;

namespace UnitTests.Manifest;

/// <summary>
/// Exercises <see cref="InProcessTestCluster.DescribePendingFetch"/> directly, addressing a Copilot-review
/// finding on dotnet/orleans#11438 that no test covered the exact formatting of this diagnostic string
/// (membership version, published version, pending silos, elapsed time, and the most recent failure).
/// </summary>
public class InProcessTestClusterPendingFetchDescriptionTests
{
    [Fact]
    public void DescribePendingFetch_WithNoRecordedAttempt_DescribesOnlyThePublishedVersion()
    {
        var observer = CreateSiloAddress(11111, 1);
        var manifest = new ClusterManifest(new MajorMinorVersion(3, 2), ImmutableDictionary<SiloAddress, GrainManifest>.Empty);

        var description = InProcessTestCluster.DescribePendingFetch(observer, manifest, diagnostics: null, observerUtcNow: DateTime.UtcNow);

        Assert.Equal($"{observer}: no manifest-update attempt recorded (published version {manifest.Version})", description);
    }

    [Fact]
    public void DescribePendingFetch_WithPendingSilosAndAFailure_IncludesVersionsPendingElapsedAndFailure()
    {
        var observer = CreateSiloAddress(11111, 1);
        var pendingPeer = CreateSiloAddress(11112, 1);
        var failedPeer = CreateSiloAddress(11113, 1);
        var manifest = new ClusterManifest(new MajorMinorVersion(5, 0), ImmutableDictionary<SiloAddress, GrainManifest>.Empty);
        var attemptStartedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var failureAt = attemptStartedAt.AddSeconds(2);
        var observerUtcNow = attemptStartedAt.AddSeconds(12.3);
        var diagnostics = new ManifestUpdateAttemptDiagnostics(
            AttemptId: 7,
            MembershipVersion: new MembershipVersion(5),
            AttemptStartedAt: attemptStartedAt,
            PendingSilos: ImmutableHashSet.Create(pendingPeer),
            LastFailedSilo: failedPeer,
            LastFailureMessage: "simulated fetch failure",
            LastFailureAt: failureAt);

        var description = InProcessTestCluster.DescribePendingFetch(observer, manifest, diagnostics, observerUtcNow);

        Assert.Equal(
            $"{observer}: membership v{diagnostics.MembershipVersion}, published v{manifest.Version}, "
            + $"pending=[{pendingPeer}], attempt started 12.3s ago"
            + $", last fetch failure for {failedPeer} at {failureAt:O}: {diagnostics.LastFailureMessage}",
            description);
    }

    private static SiloAddress CreateSiloAddress(int port, int generation) =>
        SiloAddress.New(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port), generation);
}
