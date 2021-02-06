using System;

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
            int index = sortedRingList.AsSpan().BinarySearch(new Searcher(uniformHashCode));
            if (index < 0)
            {
                index = ~index;
                // if not found in traversal, then first element should be returned (we are on a ring)
                if (index == sortedRingList.Length) index = 0;
            }
            return sortedRingList[index];
        }

        private readonly struct Searcher : IComparable<T>
        {
            private readonly uint value;
            public Searcher(uint value) => this.value = value;
            public int CompareTo(T other) => value.CompareTo(other.GetUniformHashCode());
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
