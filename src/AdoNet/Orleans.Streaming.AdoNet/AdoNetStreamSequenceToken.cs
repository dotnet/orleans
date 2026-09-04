using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.AdoNet;

[Serializable]
[GenerateSerializer]
internal sealed class AdoNetStreamSequenceToken : PartitionedStreamSequenceToken
{
    public AdoNetStreamSequenceToken(
        string serviceId,
        string providerId,
        string queueId,
        long sequenceNumber,
        int eventIndex = 0)
        : base(
            GetProviderIdentity(serviceId, providerId),
            queueId,
            sequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sequenceNumber,
            eventIndex)
    {
        ServiceId = serviceId;
        ProviderId = providerId;
        QueueId = queueId;
    }

    public AdoNetStreamSequenceToken()
    {
    }

    [Id(0)]
    public string ServiceId { get; } = null!;

    [Id(1)]
    public string ProviderId { get; } = null!;

    [Id(2)]
    public string QueueId { get; } = null!;

    public override bool Equals(StreamSequenceToken? other)
        => other is AdoNetStreamSequenceToken token
            && string.Equals(ServiceId, token.ServiceId, StringComparison.Ordinal)
            && string.Equals(ProviderId, token.ProviderId, StringComparison.Ordinal)
            && string.Equals(QueueId, token.QueueId, StringComparison.Ordinal)
            && SequenceNumber == token.SequenceNumber
            && EventIndex == token.EventIndex;

    public override bool Equals(object? obj)
        => obj is StreamSequenceToken token && Equals(token);

    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (other is not AdoNetStreamSequenceToken token
            || !string.Equals(ServiceId, token.ServiceId, StringComparison.Ordinal)
            || !string.Equals(ProviderId, token.ProviderId, StringComparison.Ordinal)
            || !string.Equals(QueueId, token.QueueId, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(other));
        }

        var difference = SequenceNumber.CompareTo(token.SequenceNumber);
        return difference != 0 ? difference : EventIndex.CompareTo(token.EventIndex);
    }

    public override int GetHashCode()
        => HashCode.Combine(ServiceId, ProviderId, QueueId, SequenceNumber, EventIndex);

    internal static string GetProviderIdentity(string serviceId, string providerId)
        => $"{serviceId.Length}:{serviceId}{providerId}";
}
