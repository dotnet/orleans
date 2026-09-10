using System.ComponentModel;
using System.Globalization;

namespace Orleans.AdvancedReminders.DynamoDB;

/// <summary>
/// Defines the billing mode and provisioned capacity of a DynamoDB advanced reminder table and its indexes.
/// </summary>
[TypeConverter(typeof(DynamoDBCapacityConverter))]
public abstract class DynamoDBCapacity
{
    private DynamoDBCapacity() { }

    /// <summary>
    /// Gets a policy which uses on-demand billing without provisioned capacity values.
    /// </summary>
    public static DynamoDBCapacity OnDemand { get; } = new OnDemandCapacity();

    /// <summary>
    /// Creates a provisioned capacity policy for the table and each global secondary index.
    /// </summary>
    /// <param name="readUnits">The positive read capacity unit count.</param>
    /// <param name="writeUnits">The positive write capacity unit count.</param>
    /// <returns>The provisioned capacity policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either capacity unit count is less than one.</exception>
    public static DynamoDBCapacity Provisioned(int readUnits, int writeUnits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(readUnits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(writeUnits, 1);
        return new ProvisionedCapacity(readUnits, writeUnits);
    }

    internal virtual bool TryGetProvisionedThroughput(out int readUnits, out int writeUnits)
    {
        readUnits = default;
        writeUnits = default;
        return false;
    }

    internal static DynamoDBCapacity Parse(string value)
    {
        var text = value.Trim();
        if (text.Equals(nameof(OnDemand), StringComparison.OrdinalIgnoreCase))
        {
            return OnDemand;
        }

        const string prefix = nameof(Provisioned) + "(";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.EndsWith(')'))
        {
            var units = text.AsSpan(prefix.Length, text.Length - prefix.Length - 1);
            var separator = units.IndexOf(',');
            if (separator >= 0
                && int.TryParse(units[..separator].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var readUnits)
                && int.TryParse(units[(separator + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var writeUnits)
                && readUnits > 0 && writeUnits > 0)
            {
                return Provisioned(readUnits, writeUnits);
            }
        }

        throw new FormatException("Expected OnDemand or Provisioned(<positive read units>,<positive write units>).");
    }

    /// <inheritdoc />
    public abstract override string ToString();

    private sealed class OnDemandCapacity : DynamoDBCapacity
    {
        public override string ToString() => nameof(OnDemand);
    }

    private sealed class ProvisionedCapacity(int readUnits, int writeUnits) : DynamoDBCapacity
    {
        internal override bool TryGetProvisionedThroughput(out int reads, out int writes)
        {
            reads = readUnits;
            writes = writeUnits;
            return true;
        }

        public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(Provisioned)}({readUnits},{writeUnits})");
    }
}
