using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Orleans.Runtime.MembershipService
{
    internal static class MembershipHelper
    {
        // pick a specified number of elements from a set of candidates
        // - in a balanced way (try to pick evenly from groups)
        // - in a deterministic way (using sorting order on candidates and keys)
        internal static List<T> DeterministicBalancedChoice<T, K>(IEnumerable<T> candidates, int count, Func<T, K> group, ILogger logger = null)
            where T : IComparable where K : IComparable
        {
            // organize candidates by groups
            var groups = new Dictionary<K, List<T>>();
            var keys = new List<K>();
            int numcandidates = 0;
            foreach (var c in candidates)
            {
                var key = group(c);
                List<T> list;
                if (!groups.TryGetValue(key, out list))
                {
                    groups[key] = list = new List<T>();
                    keys.Add(key);
                }
                list.Add(c);
                numcandidates++;
            }

            if (numcandidates < count)
                throw new ArgumentException("not enough candidates");

            // sort the keys and the groups to guarantee deterministic result
            keys.Sort();
            foreach (var kvp in groups)
                kvp.Value.Sort();

            // for debugging, trace all the gateway candidates
            if (logger != null && logger.IsEnabled(LogLevel.Trace))
            {
                var b = new StringBuilder();
                foreach (var k in keys)
                {
                    b.Append(k);
                    b.Append(':');
                    foreach (var s in groups[k])
                    {
                        b.Append(' ');
                        b.Append(s);
                    }
                }
                logger.Trace($"-DeterministicBalancedChoice candidates {b}");
            }

            // pick round-robin from groups
            var result = new List<T>();
            for (int i = 0; result.Count < count; i++)
            {
                var list = groups[keys[i % keys.Count]];
                var col = i / keys.Count;
                if (col < list.Count)
                    result.Add(list[col]);
            }
            return result;
        }
    }
}
