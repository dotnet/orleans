using System.Diagnostics.CodeAnalysis;

#nullable enable
namespace Orleans.Runtime;

internal static class DirectoryResult
{
    public static DirectoryResult<T> FromResult<T>(T result, MembershipVersion version) => new DirectoryResult<T>(result, version);
    public static DirectoryResult<T> RefreshRequired<T>(MembershipVersion version) => new DirectoryResult<T>(default, version);
}

[GenerateSerializer, Alias("DirectoryResult`1"), Immutable]
internal readonly struct DirectoryResult<T>(T? result, MembershipVersion version)
{
    [Id(0)]
    private readonly T? _result = result;

    [Id(1)]
    public readonly MembershipVersion Version = version;

    public bool TryGetResult(MembershipVersion version, [NotNullWhen(true)] out T? result)
    {
        if (Version == version)
        {
            result = _result!;
            return true;
        }

        result = default;
        return false;
    }
}
