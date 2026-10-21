using System.Diagnostics.CodeAnalysis;

namespace Orleans.Runtime;

internal enum DirectoryPartitionKind
{
    Legacy,
    Distributed,
    NamedDistributed
}

internal static class DirectoryResult
{
    public static DirectoryResult<T> FromResult<T>(T result, MembershipVersion version, DirectoryPartitionKind partitionKind = DirectoryPartitionKind.Legacy) => new(result, version, partitionKind);
    public static DirectoryResult<T> RefreshRequired<T>(MembershipVersion version, DirectoryPartitionKind partitionKind) => new(default, version, partitionKind);
    public static DirectoryResult<T> RetryAfter<T>(TimeSpan retryAfter, DirectoryPartitionKind partitionKind) => new(retryAfter, partitionKind);
}

[GenerateSerializer, Alias("DirectoryResult`1"), Immutable]
internal readonly struct DirectoryResult<T>
{
    [Id(0)]
    private readonly T? _result;

    [Id(1)]
    public readonly MembershipVersion Version;

    /// <summary>
    /// When greater than <see cref="TimeSpan.Zero"/>, indicates that the caller should retry after the specified delay.
    /// </summary>
    [Id(2)]
    public readonly TimeSpan RetryAfterDelay;

    /// <summary>
    /// Identifies the authority served by this endpoint, including whether it recovers default-directory ranges.
    /// The default value selects legacy owner-loss invalidation at the caller.
    /// </summary>
    [Id(3)]
    public readonly DirectoryPartitionKind PartitionKind;

    public DirectoryResult(T? result, MembershipVersion version, DirectoryPartitionKind partitionKind = DirectoryPartitionKind.Legacy)
    {
        _result = result;
        Version = version;
        PartitionKind = partitionKind;
    }

    public DirectoryResult(TimeSpan retryAfter, DirectoryPartitionKind partitionKind)
    {
        RetryAfterDelay = retryAfter;
        PartitionKind = partitionKind;
    }

    public bool TryGetResult(MembershipVersion version, [NotNullWhen(true)] out T? result)
    {
        if (RetryAfterDelay <= TimeSpan.Zero && Version == version)
        {
            result = _result!;
            return true;
        }

        result = default;
        return false;
    }
}
