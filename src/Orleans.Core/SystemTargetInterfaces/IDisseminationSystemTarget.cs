using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime;

internal interface IDisseminationSystemTarget : ISystemTarget
{
    [OneWay]
    Task PushGossip(DisseminationGossipBatch batch, CancellationToken cancellationToken);

    Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request, CancellationToken cancellationToken);
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationTopicDigest : IEquatable<DisseminationTopicDigest>
{
    public DisseminationTopicDigest(string key, long version)
    {
        Key = key ?? string.Empty;
        Version = version;
    }

    [Id(0)]
    public string Key { get; }

    [Id(1)]
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

[GenerateSerializer, Immutable]
internal sealed class DisseminationValue
{
    [Id(0)]
    public DisseminationTopicDigest Digest { get; init; }

    [Id(1)]
    public required SiloAddress Root { get; init; }

    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }

    [Id(3)]
    public ReadOnlyMemory<byte> Payload { get; init; } = Array.Empty<byte>();
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationGossipBatch
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public ImmutableDictionary<string, ImmutableArray<DisseminationValue>> ValuesByTopic { get; init; } =
        ImmutableDictionary<string, ImmutableArray<DisseminationValue>>.Empty;
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyRequest
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>> DigestsByTopic { get; init; } =
        ImmutableDictionary<string, ImmutableArray<DisseminationTopicDigest>>.Empty;
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyResponse
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public ImmutableDictionary<string, ImmutableArray<DisseminationValue>> ValuesByTopic { get; init; } =
        ImmutableDictionary<string, ImmutableArray<DisseminationValue>>.Empty;

    [Id(2)]
    public bool Truncated { get; init; }

    [Id(3)]
    public ImmutableArray<string> SupportedTopics { get; init; } = [];
}
