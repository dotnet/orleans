using Orleans.Providers.Streams.Common;

namespace Orleans.Streaming.AdoNet;

[Serializable]
[GenerateSerializer]
internal sealed class AdoNetStreamSequenceToken : EventSequenceTokenV2, IPartitionedStreamSequenceToken
{
    public AdoNetStreamSequenceToken(
        string serviceId,
        string providerId,
        string queueId,
        long sequenceNumber,
        int eventIndex = 0)
        : base(sequenceNumber, eventIndex)
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

    string? IPartitionedStreamSequenceToken.ProviderIdentity
        => GetProviderIdentity(ServiceId, ProviderId);

    string? IPartitionedStreamSequenceToken.PartitionIdentity => QueueId;

    string IPartitionedStreamSequenceToken.Position
        => SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override bool Equals(StreamSequenceToken? other)
        => other is IPartitionedStreamSequenceToken token
            && string.Equals(
                GetProviderIdentity(ServiceId, ProviderId),
                token.ProviderIdentity,
                StringComparison.Ordinal)
            && string.Equals(QueueId, token.PartitionIdentity, StringComparison.Ordinal)
            && ComparePositions(
                SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                token.Position) == 0
            && EventIndex == ((StreamSequenceToken)token).EventIndex;

    public override bool Equals(object? obj)
        => obj is StreamSequenceToken token && Equals(token);

    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (other is not IPartitionedStreamSequenceToken token
            || !string.Equals(
                GetProviderIdentity(ServiceId, ProviderId),
                token.ProviderIdentity,
                StringComparison.Ordinal)
            || !string.Equals(QueueId, token.PartitionIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(other));
        }

        var difference = ComparePositions(
            SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token.Position);
        return difference != 0 ? difference : EventIndex.CompareTo(((StreamSequenceToken)token).EventIndex);
    }

    public override int GetHashCode()
        => HashCode.Combine(
            GetProviderIdentity(ServiceId, ProviderId),
            QueueId,
            GetPositionHashCode(
                SequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            EventIndex);

    internal static string GetProviderIdentity(string serviceId, string providerId)
        => $"{serviceId.Length}:{serviceId}{providerId}";

    private static int ComparePositions(string left, string right)
    {
        var leftStart = 0;
        while (leftStart < left.Length && left[leftStart] == '0')
        {
            leftStart++;
        }

        var rightStart = 0;
        while (rightStart < right.Length && right[rightStart] == '0')
        {
            rightStart++;
        }

        var lengthComparison = (left.Length - leftStart).CompareTo(right.Length - rightStart);
        return lengthComparison != 0
            ? lengthComparison
            : left.AsSpan(leftStart).SequenceCompareTo(right.AsSpan(rightStart));
    }

    private static int GetPositionHashCode(string position)
    {
        var start = 0;
        while (start < position.Length && position[start] == '0')
        {
            start++;
        }

        return string.GetHashCode(position.AsSpan(start), StringComparison.Ordinal);
    }
}
