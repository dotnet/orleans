using System.ComponentModel;
using System.Globalization;

namespace Orleans.AdvancedReminders.Redis;

/// <summary>
/// Defines inactivity expiry for the entire Redis advanced reminder table.
/// </summary>
[TypeConverter(typeof(ReminderTableExpiryConverter))]
public abstract class ReminderTableExpiry
{
    private ReminderTableExpiry() { }

    /// <summary>
    /// Gets a policy which keeps the table without an expiry deadline.
    /// </summary>
    public static ReminderTableExpiry Never { get; } = new NeverExpiry();

    /// <summary>
    /// Expires the whole table after the specified time without a successful upsert.
    /// </summary>
    /// <param name="inactivityPeriod">A positive inactivity period.</param>
    /// <returns>The table inactivity expiry policy.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The inactivity period is not positive.</exception>
    public static ReminderTableExpiry AfterInactivity(TimeSpan inactivityPeriod)
    {
        if (inactivityPeriod <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(inactivityPeriod), inactivityPeriod, "The inactivity period must be positive.");
        }

        return new InactivityExpiry(inactivityPeriod);
    }

    internal virtual bool TryGetInactivityPeriod(out TimeSpan inactivityPeriod)
    {
        inactivityPeriod = default;
        return false;
    }

    internal static ReminderTableExpiry Parse(string value)
    {
        var text = value.Trim();
        if (text.Equals(nameof(Never), StringComparison.OrdinalIgnoreCase))
        {
            return Never;
        }

        const string prefix = nameof(AfterInactivity) + "(";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && text.EndsWith(')')
            && TimeSpan.TryParseExact(text.AsSpan(prefix.Length, text.Length - prefix.Length - 1).Trim(),
                "c", CultureInfo.InvariantCulture, out var inactivityPeriod)
            && inactivityPeriod > TimeSpan.Zero)
        {
            return AfterInactivity(inactivityPeriod);
        }

        throw new FormatException("Expected Never or AfterInactivity(<positive invariant TimeSpan>).");
    }

    /// <inheritdoc />
    public abstract override string ToString();

    private sealed class NeverExpiry : ReminderTableExpiry
    {
        public override string ToString() => nameof(Never);
    }

    private sealed class InactivityExpiry(TimeSpan inactivityPeriod) : ReminderTableExpiry
    {
        internal override bool TryGetInactivityPeriod(out TimeSpan period)
        {
            period = inactivityPeriod;
            return true;
        }

        public override string ToString() => string.Concat(nameof(AfterInactivity), "(", inactivityPeriod.ToString("c", CultureInfo.InvariantCulture), ")");
    }
}
