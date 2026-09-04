using System;
using System.Collections.Generic;
using Orleans.Dashboard.Model;

namespace Orleans.Dashboard.Metrics.History;

internal sealed class GrainTraceEqualityComparer : IEqualityComparer<GrainTraceEntry>
{
    private readonly bool _withSiloAddress;

    public static readonly GrainTraceEqualityComparer ByGrainAndMethod = new(false);

    public static readonly GrainTraceEqualityComparer ByGrainAndMethodAndSilo = new(true);

    private GrainTraceEqualityComparer(bool withSiloAddress)
    {
        _withSiloAddress = withSiloAddress;
    }

    public bool Equals(GrainTraceEntry? x, GrainTraceEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x == null || y == null)
        {
            return false;
        }

        var isEquals =
            string.Equals(x.Grain, y.Grain, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Method, y.Method, StringComparison.OrdinalIgnoreCase);

        if (_withSiloAddress)
        {
            isEquals &= string.Equals(x.SiloAddress, y.SiloAddress, StringComparison.OrdinalIgnoreCase);
        }

        return isEquals;
    }

    public int GetHashCode(GrainTraceEntry obj)
    {
        if (obj == null)
        {
            return 0;
        }

        var hashCode = 17;

        if (obj.Grain is { } grain)
        {
            hashCode = hashCode * 23 + grain.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        if (obj.Method is { } method)
        {
            hashCode = hashCode * 23 + method.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        if (_withSiloAddress && obj.SiloAddress is { } siloAddress)
        {
            hashCode = hashCode * 23 + siloAddress.GetHashCode(StringComparison.OrdinalIgnoreCase);
        }

        return hashCode;
    }
}
