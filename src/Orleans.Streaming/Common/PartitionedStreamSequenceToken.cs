using System.Globalization;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common;

/// <summary>
/// Exposes provider and partition identity for a stream sequence token.
/// </summary>
public interface IPartitionedStreamSequenceToken
{
    /// <summary>
    /// Gets the provider identity.
    /// </summary>
    string? ProviderIdentity { get; }

    /// <summary>
    /// Gets the partition identity.
    /// </summary>
    string? PartitionIdentity { get; }

    /// <summary>
    /// Gets the provider position.
    /// </summary>
    string Position { get; }
}

/// <summary>
/// Represents a provider and partition scoped stream position which can be persisted as JSON.
/// </summary>
[Serializable]
[GenerateSerializer]
public class PartitionedStreamSequenceToken : EventSequenceTokenV2, IPartitionedStreamSequenceToken
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionedStreamSequenceToken"/> class.
    /// </summary>
    /// <param name="providerIdentity">The provider identity.</param>
    /// <param name="partitionIdentity">The partition identity.</param>
    /// <param name="position">The provider position.</param>
    /// <param name="sequenceNumber">The receiver-local sequence number.</param>
    /// <param name="eventIndex">The event index within the source record.</param>
    public PartitionedStreamSequenceToken(
        string? providerIdentity,
        string? partitionIdentity,
        string position,
        long sequenceNumber,
        int eventIndex = 0)
        : base(sequenceNumber, eventIndex)
    {
        ProviderIdentity = providerIdentity;
        PartitionIdentity = partitionIdentity;
        Position = position ?? throw new ArgumentNullException(nameof(position));
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartitionedStreamSequenceToken"/> class.
    /// </summary>
    /// <remarks>This constructor is for serializer use.</remarks>
    public PartitionedStreamSequenceToken()
    {
    }

    /// <summary>
    /// Gets the provider identity.
    /// </summary>
    [Id(2)]
    public string? ProviderIdentity { get; } = null!;

    /// <summary>
    /// Gets the partition identity.
    /// </summary>
    [Id(3)]
    public string? PartitionIdentity { get; } = null!;

    /// <summary>
    /// Gets the provider position.
    /// </summary>
    [Id(4)]
    public string Position { get; } = null!;

    /// <inheritdoc />
    public override bool Equals(StreamSequenceToken? other)
        => other is IPartitionedStreamSequenceToken token
            && string.Equals(ProviderIdentity, token.ProviderIdentity, StringComparison.Ordinal)
            && string.Equals(PartitionIdentity, token.PartitionIdentity, StringComparison.Ordinal)
            && ComparePositions(Position, token.Position) == 0
            && EventIndex == ((StreamSequenceToken)token).EventIndex;

    /// <inheritdoc />
    public override bool Equals(object? obj)
        => obj is StreamSequenceToken token && Equals(token);

    /// <inheritdoc />
    public override int CompareTo(StreamSequenceToken? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (other is not IPartitionedStreamSequenceToken token
            || !string.Equals(ProviderIdentity, token.ProviderIdentity, StringComparison.Ordinal)
            || !string.Equals(PartitionIdentity, token.PartitionIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(other));
        }

        var difference = ComparePositions(Position, token.Position);
        return difference != 0 ? difference : EventIndex.CompareTo(((StreamSequenceToken)token).EventIndex);
    }

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(
            ProviderIdentity,
            PartitionIdentity,
            GetPositionHashCode(Position),
            EventIndex);

    /// <inheritdoc />
    public override string ToString()
        => string.Format(
            CultureInfo.InvariantCulture,
            "PartitionedStreamSequenceToken(Provider: {0}, Partition: {1}, Position: {2}, SequenceNumber: {3}, EventIndex: {4})",
            ProviderIdentity,
            PartitionIdentity,
            Position,
            SequenceNumber,
            EventIndex);

    internal static int ComparePositions(string left, string right)
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

        var leftIsNumeric = left[leftStart..].All(char.IsAsciiDigit);
        var rightIsNumeric = right[rightStart..].All(char.IsAsciiDigit);
        if (leftIsNumeric && rightIsNumeric)
        {
            var lengthComparison = (left.Length - leftStart).CompareTo(right.Length - rightStart);
            return lengthComparison != 0
                ? lengthComparison
                : left.AsSpan(leftStart).SequenceCompareTo(right.AsSpan(rightStart));
        }

        return string.CompareOrdinal(left, right);
    }

    internal static int GetPositionHashCode(string position)
    {
        var start = 0;
        while (start < position.Length && position[start] == '0')
        {
            start++;
        }

        var value = position.AsSpan(start);
        return value.Length == 0 || value.IndexOfAnyExceptInRange('0', '9') < 0
            ? string.GetHashCode(value, StringComparison.Ordinal)
            : StringComparer.Ordinal.GetHashCode(position);
    }
}
