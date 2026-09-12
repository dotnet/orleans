using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime;

/// <summary>
/// Centralized statistics on per-grain-type activation counts.
/// </summary>
internal class GrainCountStatistics(GrainInstruments instruments)
{
    public IEnumerable<KeyValuePair<string, long>> GetSimpleGrainStatistics()
    {
        return instruments
            .GrainCounts
            .Select(s => new KeyValuePair<string, long>(s.Key, s.Value))
            .Where(p => p.Value > 0);
    }
}
