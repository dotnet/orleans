using System.Reflection;

namespace Orleans.Dissemination.PerformanceHarness;

internal sealed record AggregationFanout(int Value, string Source)
{
    public static AggregationFanout Read(object overlay, bool aggregationTree, int memberCount, Func<int> legacyFanout)
    {
        if (!aggregationTree)
        {
            return new(legacyFanout(), "MembershipFanout");
        }

        var type = overlay.GetType();
        var property = type.GetProperty("AggregationFanOutFactor", BindingFlags.Instance | BindingFlags.Public);
        if (property is null)
        {
            return new(legacyFanout(), "LegacyAggregationMembershipFanout");
        }

        if (property.PropertyType != typeof(int) || property.GetIndexParameters().Length != 0
            || property.GetMethod is not { IsPublic: true }
            || property.GetValue(overlay) is not int value)
        {
            throw new InvalidOperationException(
                $"Selected runtime '{type.Assembly.FullName}' must expose a public int {type.FullName}.AggregationFanOutFactor. "
                + "Update the performance harness runtime adapter for this candidate.");
        }

        return new(Math.Clamp(value, 1, Math.Max(1, memberCount)), "Overlay.AggregationFanOutFactor");
    }
}
