using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Statistics;

internal static class EnvironmentStatisticExtensions
{
    public static bool IsValid(this EnvironmentStatistics statistics)
        => statistics.MaximumAvailableMemoryBytes > 0;
}
