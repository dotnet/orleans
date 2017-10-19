using System;
using System.Collections.Generic;
using System.Linq;


namespace Orleans.Runtime
{
    internal interface IRingIdentifier<T> : IEquatable<T>
    {
        uint GetUniformHashCode();
    }

    internal class HashRing<T>
    {
        private readonly List<IRingIdentifier<T>> sortedRingList;
        private readonly object lockable = new object();

        internal HashRing()
        {
            sortedRingList = new List<IRingIdentifier<T>>();
        }

        public HashRing(IEnumerable<IRingIdentifier<T>> ring)
        {
            var tmpList = new List<IRingIdentifier<T>>(ring);
            tmpList.Sort((x, y) => x.GetUniformHashCode().CompareTo(y.GetUniformHashCode()));
            sortedRingList = tmpList; // make it read only, so can't add any more elements if created via this constructor.
        }

        public IEnumerable<T> GetAllRingMembers()
        {
            IEnumerable<T> copy;
            lock (lockable)
            {
                copy = sortedRingList.Cast<T>().ToArray();
            }
            return copy;
        }

        internal void AddElement(IRingIdentifier<T> element)
        {
            lock (lockable)
            {
                if (sortedRingList.Contains(element))
                {
                    // we already have this element
                    return;
                }

                uint hash = element.GetUniformHashCode();

                // insert new element in the sorted order
                // Find the last element with hash smaller than the new element, and insert the latter after (this is why we have +1 here) the former.
                // Notice that FindLastIndex might return -1 if this should be the first element in the list, but then
                // 'index' will get 0, as needed.
                int index = sortedRingList.FindLastIndex(elem => elem.GetUniformHashCode() < hash) + 1;

                sortedRingList.Insert(index, element);
            }
        }

        internal void RemoveElement(IRingIdentifier<T> element)
        {
            throw new NotImplementedException();
        }

        public T CalculateResponsible<R>(IRingIdentifier<R> element)
        {
            return CalculateResponsible(element.GetUniformHashCode());     
        }

        public T CalculateResponsible(Guid guid)
        {
            byte[] guidBytes = guid.ToByteArray();
            uint uniformHashCode = JenkinsHash.ComputeHash(guidBytes);
            return CalculateResponsible(uniformHashCode);   
        }

        private T CalculateResponsible(uint uniformHashCode)
        {
            lock (lockable)
            {
                if (sortedRingList.Count == 0)
                {
                    // empty ring.
                    return default(T);
                }

                // use clockwise ... current code in membershipOracle.CalculateTargetSilo() does counter-clockwise ...
                // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
                int index = sortedRingList.FindIndex(elem => (elem.GetUniformHashCode() >= uniformHashCode));
                if (index == -1)
                {
                    // if not found in traversal, then first element should be returned (we are on a ring)
                    return (T)sortedRingList.First();
                }
                else
                {
                    return (T)sortedRingList[index];
                }
            }
        }

        public override string ToString()
        {
            lock (lockable)
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
}
