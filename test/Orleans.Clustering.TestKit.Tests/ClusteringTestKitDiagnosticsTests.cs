using Xunit;

namespace Orleans.Clustering.TestKit.Tests;

public sealed class ClusteringTestKitDiagnosticsTests
{
    [Fact]
    public void CreateFailure_ReportsProviderGuaranteeHandleClusterAndIdentity()
    {
        var failure = ClusteringTestKitDiagnostics.CreateFailure("Local", "G01", "cluster-a", "A2", 41, "identity=127.0.0.1:12000@1");
        Assert.Contains("provider=Local; guarantee=G01; cluster=cluster-a; handle=A2; seed=41", failure.Message);
        Assert.Contains("identity=127.0.0.1:12000@1", failure.Message);
    }

    [Fact]
    public void CreateFailure_ReportsExpectedObservedFieldsVersionsAndTokenModes()
    {
        var failure = ClusteringTestKitDiagnostics.CreateFailure("Local", "G12", "a", "A1", 7,
            "Status expected=Active observed=Joining; version expected=3 observed=4; row-mode=previous; table-mode=previous; row ETag=opaque/a");
        Assert.Contains("Status expected=Active observed=Joining", failure.Message);
        Assert.Contains("version expected=3 observed=4", failure.Message);
        Assert.Contains("row-mode=previous; table-mode=previous; row ETag=opaque/a", failure.Message);
    }

    [Fact]
    public void FormatHistory_ReportsSeedCaseOperationPrefixAndOwnerHeartbeat()
    {
        var history = ClusteringTestKitDiagnostics.FormatHistory(17, 9, ["InsertNew", "HeartbeatAdvance", "UpdateAfterHeartbeat"], "owner heartbeat=2024-01-02T03:06:00Z");
        Assert.Equal("seed=17; case=9; prefix=[InsertNew -> HeartbeatAdvance -> UpdateAfterHeartbeat]; owner heartbeat=2024-01-02T03:06:00Z", history);
        Assert.DoesNotContain("System.Collections", history);
    }

    [Fact]
    public void AttachCleanupFailure_PreservesPrimaryExceptionAndAddsCleanupContext()
    {
        var primary = new ClusteringConformanceException("failed +1 guarantee");
        var cleanup = new InvalidOperationException("cleanup cluster=a");
        ClusteringTestKitDiagnostics.AttachCleanupFailure(primary, cleanup);
        Assert.Same(cleanup, primary.Data[ClusteringTestKitDiagnostics.CleanupFailureKey]);
        Assert.Equal("failed +1 guarantee", primary.Message);
        Assert.Null(primary.InnerException);
    }

    [Fact]
    public void CreateFailure_DoesNotIncludeConnectionSecrets()
    {
        // No backend/options/connection object is accepted by the formatter.
        var failure = ClusteringTestKitDiagnostics.CreateFailure("Provider", "G01", "safe-cluster", "A1", 3, "HostName mismatch");
        Assert.Contains("safe-cluster", failure.Message);
        Assert.DoesNotContain("ConnectionString", failure.Message);
        Assert.DoesNotContain("Password=", failure.Message);
        Assert.DoesNotContain("AccountKey=", failure.Message);
    }
}
