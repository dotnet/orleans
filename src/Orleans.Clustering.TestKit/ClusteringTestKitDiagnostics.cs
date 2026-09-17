namespace Orleans.Clustering.TestKit;

/// <summary>An unconditional membership contract violation, independent of any test framework.</summary>
public sealed class ClusteringConformanceException : Exception
{
    /// <summary>Creates a conformance failure with actionable observation details.</summary>
    public ClusteringConformanceException(string message) : base(message) { }

    /// <summary>Creates a conformance failure retaining the underlying operation failure.</summary>
    public ClusteringConformanceException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class ClusteringTestKitDiagnostics
{
    internal const string CleanupFailureKey = "ClusteringTestKit.CleanupFailure";

    internal static ClusteringConformanceException CreateFailure(
        string provider, string guarantee, string cluster, string handle, int seed, string detail, Exception? innerException = null)
    {
        var message = $"Membership conformance: provider={provider}; guarantee={guarantee}; cluster={cluster}; handle={handle}; seed={seed}; {detail}";
        return innerException is null ? new(message) : new(message, innerException);
    }

    internal static string FormatHistory(int seed, int caseNumber, IEnumerable<string> prefix, string detail)
        => $"seed={seed}; case={caseNumber}; prefix=[{string.Join(" -> ", prefix)}]; {detail}";

    internal static void AttachCleanupFailure(Exception primary, Exception cleanup)
        => primary.Data[CleanupFailureKey] = cleanup;

    internal static void Require(bool condition, string detail)
    {
        if (!condition)
        {
            throw new ClusteringConformanceException(detail);
        }
    }
}
