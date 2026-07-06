using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;

namespace Orleans.Runtime.Dissemination;

internal interface IDisseminationTopic
{
    string Name { get; }

    DisseminationMembershipScope MembershipScope { get; }

    DisseminationTopicOptions Options { get; }

    bool IsEnabled { get; }

    IReadOnlyList<DisseminationTopicDigest> GetDigests();

    int CompareVersion(DisseminationDigest left, DisseminationDigest right);

    bool IsObsolete(DisseminationDigest digest);

    ValueTask<DisseminationValue?> GetValue(
        DisseminationDigest digest,
        DisseminationDigest? peerDigest,
        CancellationToken cancellationToken);

    ValueTask<DisseminationApplyResult> ApplyValue(DisseminationValue value, CancellationToken cancellationToken);

    ValueTask OnFallbackRequired(SiloAddress? peer, DisseminationDigest digest, CancellationToken cancellationToken);
}

internal enum DisseminationMembershipScope
{
    ActiveMembers,
    AllMembers,
}

internal readonly struct DisseminationTopicDigest : IEquatable<DisseminationTopicDigest>
{
    public DisseminationTopicDigest(string key, long version)
    {
        Key = key ?? string.Empty;
        Version = version;
    }

    public string Key { get; }

    public long Version { get; }

    public bool Equals(DisseminationTopicDigest other) =>
        string.Equals(Key, other.Key, StringComparison.Ordinal)
        && Version == other.Version;

    public override bool Equals(object? obj) => obj is DisseminationTopicDigest other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Key ?? string.Empty),
        Version);

    public override string ToString() => $"{Key}/{Version}";

    public static bool operator ==(DisseminationTopicDigest left, DisseminationTopicDigest right) => left.Equals(right);

    public static bool operator !=(DisseminationTopicDigest left, DisseminationTopicDigest right) => !left.Equals(right);
}
