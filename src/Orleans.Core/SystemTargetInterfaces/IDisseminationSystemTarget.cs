using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Threading;
using Orleans.Concurrency;

namespace Orleans.Runtime;

internal interface IDisseminationSystemTarget : ISystemTarget
{
    [OneWay]
    Task PushBroadcast(DisseminationBroadcastBatch batch, CancellationToken cancellationToken);

    Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request, CancellationToken cancellationToken);
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationValue
{
    public DisseminationValue(string key, long fromVersion, long toVersion, ReadOnlyMemory<byte> payload)
    {
        Key = key ?? string.Empty;
        FromVersion = fromVersion;
        ToVersion = toVersion;
        Payload = payload;
    }

    [Id(0)]
    public string Key { get; }

    [Id(1)]
    public long FromVersion { get; }

    [Id(2)]
    public long ToVersion { get; }

    [Id(3)]
    public ReadOnlyMemory<byte> Payload { get; }
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationBroadcastValue
{
    [Id(0)]
    public DisseminationValue Value { get; init; }

    [Id(1)]
    public required SiloAddress Originator { get; init; }

    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationBroadcastBatch
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public FrozenDictionary<string, ImmutableArray<DisseminationBroadcastValue>> ValuesByNamespace { get; init; } =
        FrozenDictionary<string, ImmutableArray<DisseminationBroadcastValue>>.Empty;
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyRequest
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public FrozenDictionary<string, FrozenDictionary<string, long>> Digest { get; init; } =
        FrozenDictionary<string, FrozenDictionary<string, long>>.Empty;
}

[GenerateSerializer, Immutable]
internal sealed class DisseminationAntiEntropyResponse
{
    [Id(0)]
    public required SiloAddress Sender { get; init; }

    [Id(1)]
    public FrozenDictionary<string, ImmutableArray<DisseminationBroadcastValue>> ValuesByNamespace { get; init; } =
        FrozenDictionary<string, ImmutableArray<DisseminationBroadcastValue>>.Empty;

    [Id(2)]
    public bool Truncated { get; init; }
}
