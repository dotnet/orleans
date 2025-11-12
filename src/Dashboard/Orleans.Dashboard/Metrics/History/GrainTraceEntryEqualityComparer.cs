using Orleans.Dashboard.Model;
using System;
using System.Collections.Generic;

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

    public bool Equals(GrainTraceEntry x, GrainTraceEntry y)
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

        if (obj.Grain != null)
        {
            hashCode = hashCode * 23 + (obj.Grain?.GetHashCode() ?? 0);
        }

        if (obj.Grain != null)
        {
            hashCode = hashCode * 23 + (obj.Method?.GetHashCode() ?? 0);
        }

        if (obj.Grain != null && _withSiloAddress)
        {
            hashCode = hashCode * 23 + (obj.SiloAddress?.GetHashCode() ?? 0);
        }

        return hashCode;
    }
}
