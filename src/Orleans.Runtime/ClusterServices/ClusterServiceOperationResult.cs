namespace Orleans.Runtime.ClusterServices;

internal enum ClusterServiceExecutionDisposition
{
    RejectedBeforeExecution,
    Executed,
    OutcomeUnknown
}

internal enum ClusterServiceRetryReason
{
    None,
    WrongView,
    PartitionNotReady,
    SafetyDelay,
    MemberUnavailable
}

[GenerateSerializer, Immutable, Alias("ClusterServiceOperationResult`1")]
internal readonly struct ClusterServiceOperationResult<T>
{
    private ClusterServiceOperationResult(
        T? value,
        ClusterServiceViewId viewId,
        ClusterServiceExecutionDisposition disposition,
        ClusterServiceRetryReason retryReason,
        TimeSpan retryAfter)
    {
        Value = value;
        ViewId = viewId;
        Disposition = disposition;
        RetryReason = retryReason;
        RetryAfter = retryAfter;
    }

    [Id(0)]
    public T? Value { get; }

    [Id(1)]
    public ClusterServiceViewId ViewId { get; }

    [Id(2)]
    public ClusterServiceExecutionDisposition Disposition { get; }

    [Id(3)]
    public ClusterServiceRetryReason RetryReason { get; }

    [Id(4)]
    public TimeSpan RetryAfter { get; }

    public bool CanRetryWithoutDeduplication =>
        Disposition == ClusterServiceExecutionDisposition.RejectedBeforeExecution;

    public static ClusterServiceOperationResult<T> Executed(T value, ClusterServiceViewId viewId) =>
        new(value, viewId, ClusterServiceExecutionDisposition.Executed, ClusterServiceRetryReason.None, TimeSpan.Zero);

    public static ClusterServiceOperationResult<T> Rejected(
        ClusterServiceViewId viewId,
        ClusterServiceRetryReason reason,
        TimeSpan retryAfter = default)
    {
        if (reason == ClusterServiceRetryReason.None)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter));
        }

        return new(
            default,
            viewId,
            ClusterServiceExecutionDisposition.RejectedBeforeExecution,
            reason,
            retryAfter);
    }

    public static ClusterServiceOperationResult<T> Unknown(ClusterServiceViewId viewId) =>
        new(
            default,
            viewId,
            ClusterServiceExecutionDisposition.OutcomeUnknown,
            ClusterServiceRetryReason.MemberUnavailable,
            TimeSpan.Zero);
}
