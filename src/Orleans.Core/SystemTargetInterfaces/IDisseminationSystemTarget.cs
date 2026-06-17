using System;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Runtime;

internal interface IDisseminationSystemTarget : ISystemTarget
{
    Task<DisseminationCapabilityResponse> GetCapabilities(DisseminationCapabilityRequest request);

    [OneWay]
    Task PushGossip(DisseminationGossipBatch batch);

    Task<DisseminationAntiEntropyResponse> ExchangeAntiEntropy(DisseminationAntiEntropyRequest request);
}

[GenerateSerializer]
internal sealed class DisseminationCapabilityRequest
{
    [Id(0)]
    public string Topic { get; init; } = string.Empty;

    [Id(1)]
    public int ProtocolVersion { get; init; }

    [Id(2)]
    public string[] PayloadKinds { get; init; } = Array.Empty<string>();
}

[GenerateSerializer]
internal sealed class DisseminationCapabilityResponse
{
    [Id(0)]
    public string Topic { get; init; } = string.Empty;

    [Id(1)]
    public int ProtocolVersion { get; init; }

    [Id(2)]
    public bool Supported { get; init; }

    [Id(3)]
    public string[] PayloadKinds { get; init; } = Array.Empty<string>();
}

[GenerateSerializer, Immutable]
internal readonly struct DisseminationDigest : IEquatable<DisseminationDigest>
{
    public DisseminationDigest(string topic, string key, long version, string payloadKind)
    {
        Topic = topic ?? string.Empty;
        Key = key ?? string.Empty;
        Version = version;
        PayloadKind = payloadKind ?? string.Empty;
    }

    [Id(0)]
    public string Topic { get; }

    [Id(1)]
    public string Key { get; }

    [Id(2)]
    public long Version { get; }

    [Id(3)]
    public string PayloadKind { get; }

    public bool Equals(DisseminationDigest other) =>
        string.Equals(Topic, other.Topic, StringComparison.Ordinal)
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && Version == other.Version
        && string.Equals(PayloadKind, other.PayloadKind, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is DisseminationDigest other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(Topic ?? string.Empty),
        StringComparer.Ordinal.GetHashCode(Key ?? string.Empty),
        Version,
        StringComparer.Ordinal.GetHashCode(PayloadKind ?? string.Empty));

    public override string ToString() => $"{Topic}/{Key}/{Version}/{PayloadKind}";

    public static bool operator ==(DisseminationDigest left, DisseminationDigest right) => left.Equals(right);

    public static bool operator !=(DisseminationDigest left, DisseminationDigest right) => !left.Equals(right);
}

[GenerateSerializer]
internal sealed class DisseminationValue
{
    [Id(0)]
    public DisseminationDigest Digest { get; init; }

    [Id(1)]
    public SiloAddress Root { get; init; } = default!;

    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }

    [Id(3)]
    public byte[] Payload { get; init; } = Array.Empty<byte>();
}

[GenerateSerializer]
internal sealed class DisseminationGossipBatch
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationValue[] Values { get; init; } = Array.Empty<DisseminationValue>();
}

[GenerateSerializer]
internal sealed class DisseminationAntiEntropyRequest
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationCapabilityRequest[] Topics { get; init; } = Array.Empty<DisseminationCapabilityRequest>();

    [Id(2)]
    public DisseminationDigest[] Digests { get; init; } = Array.Empty<DisseminationDigest>();
}

[GenerateSerializer]
internal sealed class DisseminationAntiEntropyResponse
{
    [Id(0)]
    public SiloAddress Sender { get; init; } = default!;

    [Id(1)]
    public DisseminationValue[] Values { get; init; } = Array.Empty<DisseminationValue>();

    [Id(2)]
    public bool Truncated { get; init; }
}
