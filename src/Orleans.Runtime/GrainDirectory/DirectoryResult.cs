using System.Diagnostics.CodeAnalysis;

#nullable enable
namespace Orleans.Runtime;

[GenerateSerializer, Alias("DirectoryResult`1"), Immutable]
public readonly struct DirectoryResult<T>(T result, MembershipVersion version)
{
    [Id(0)]
    private readonly T _result = result;

    [Id(1)]
    public readonly MembershipVersion Version = version;

    public bool TryGetResult(MembershipVersion version, [NotNullWhen(true)] out T? result)
    {
        if (Version != version)
        {
            result = default;
            return false;
        }

        result = _result!;
        return true;
    }
}
