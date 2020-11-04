using System;
using System.Collections.Generic;

namespace Orleans.Runtime
{
    internal readonly struct HashRing<T> where T : class, IRingIdentifier<T>
    {
        private readonly T[] sortedRingList;

        public HashRing(T[] ring)
        {
            Array.Sort(ring, (x, y) => x.GetUniformHashCode().CompareTo(y.GetUniformHashCode()));
            sortedRingList = ring;
        }

        public T[] GetAllRingMembers() => sortedRingList;

        public T CalculateResponsible(uint uniformHashCode)
        {
            if (sortedRingList.Length == 0)
            {
                // empty ring.
                return null;
            }

            // use clockwise ... current code in membershipOracle.CalculateTargetSilo() does counter-clockwise ...
            // use null as a sentinel value that will be substituted by uniformHashCode during the binary search
            int index = Array.BinarySearch(sortedRingList, null, new Searcher(uniformHashCode));
            if (index < 0)
            {
                index = ~index;
                // if not found in traversal, then first element should be returned (we are on a ring)
                if (index == sortedRingList.Length) index = 0;
            }
            return sortedRingList[index];
        }

        private sealed class Searcher : IComparer<T>
        {
            private readonly uint value;
            public Searcher(uint value) => this.value = value;

            public int Compare(T x, T y) => (x?.GetUniformHashCode() ?? value).CompareTo(y?.GetUniformHashCode() ?? value);
        }

        public override string ToString()
        {
            return String.Format("All {0}:" + Environment.NewLine + "{1}",
                typeof(T).Name,
                Utils.EnumerableToString(
                    sortedRingList,
                    elem => String.Format("{0}/x{1,8:X8}", elem, elem.GetUniformHashCode()),
                    Environment.NewLine,
                    false));
        }
    }
}
