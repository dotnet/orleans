using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.Runtime.ConsistentRing;

namespace UnitTests.General
{
    [TestClass]
    public class RingTests_Standalone
    {
        public RingTests_Standalone()
        {
            Console.WriteLine("RingTests_Standalone - Class Constructor");
        }

        private const int count = 5;

        [TestMethod, TestCategory("Functional"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void RingStandalone_Basic()
        {
            Dictionary<SiloAddress, ConsistentRingProvider> rings = CreateServers(count);
            rings = Combine(rings, rings);
            VerifyRing(rings);
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void RingStandalone_Failures()
        {
            TestChurn(new int[] { 0 }, new int[] { });    // 1 failure in the beginning
            TestChurn(new int[] { 0, 1 }, new int[] { }); // 2 failures in the beginning

            TestChurn(new int[] { count - 1 }, new int[] { });            // 1 failure at the end
            TestChurn(new int[] { count - 1, count - 2 }, new int[] { }); // 2 failures at the end

            TestChurn(new int[] { count / 2 }, new int[] { });                // 1 failure in the middle
            TestChurn(new int[] { count / 2, 1 + count / 2 }, new int[] { }); // 2 failures in the middle

            TestChurn(new int[] { 1, count - 2 }, new int[] { }); // 2 failures at some distance
            TestChurn(new int[] { 0, count - 1 }, new int[] { }); // 2 failures at some distance
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void RingStandalone_Joins()
        {
            TestChurn(new int[] { }, new int[] { 0 });     // 1 join in the beginning
            TestChurn(new int[] { }, new int[] { 0, 1 });  // 2 joins in the beginning

            TestChurn(new int[] { }, new int[] { count - 1 });             // 1 join at the end
            TestChurn(new int[] { }, new int[] { count - 1, count - 2 });  // 2 joins at the end

            TestChurn(new int[] { }, new int[] { count / 2 });                 // 1 join in the middle
            TestChurn(new int[] { }, new int[] { count / 2, 1 + count / 2 });  // 2 joins in the middle

            TestChurn(new int[] { }, new int[] { 1, count - 2 });  // 2 joins at some distance
            TestChurn(new int[] { }, new int[] { 0, count - 1 });  // 2 joins at some distance
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void RingStandalone_Mixed()
        {
            TestChurn(new int[] { 0 }, new int[] { 1 });     // FJ in the beginning
            TestChurn(new int[] { 1 }, new int[] { 0 });     // JF in the beginning

            TestChurn(new int[] { count - 2 }, new int[] { count - 1 });     // FJ at the end
            TestChurn(new int[] { count - 1 }, new int[] { count - 2 });     // JF at the end

            TestChurn(new int[] { count / 2 }, new int[] { 1 + count / 2 });     // FJ in the middle
            TestChurn(new int[] { 1 + count / 2 }, new int[] { count / 2 });     // JF in the middle

            TestChurn(new int[] { 0 }, new int[] { count - 1 });     // F first, J at the end
            TestChurn(new int[] { count - 1 }, new int[] { 0 });     // F last, J at the beginning

        }

        private void TestChurn(int[] indexesFails, int[] indexesJoins)
        {
            Dictionary<SiloAddress, ConsistentRingProvider> rings, holder = new Dictionary<SiloAddress, ConsistentRingProvider>();
            List<SiloAddress> sortedServers, fail = new List<SiloAddress>();

            rings = CreateServers(count);

            sortedServers = rings.Keys.ToList();
            sortedServers.Sort(CompareSiloAddressesByHash);
            // failures
            foreach (int i in indexesFails)
            {
                fail.Add(sortedServers[i]);
            }
            // joins
            foreach (int i in indexesJoins)
            {
                holder.Add(sortedServers[i], rings[sortedServers[i]]);
                rings.Remove(sortedServers[i]);
            }
            rings = Combine(rings, rings);
            RemoveServers(rings, fail);     // fail nodes
            rings = Combine(rings, holder); // join the new nodes
            VerifyRing(rings);
        }

        #region Util methods

        private Dictionary<SiloAddress, ConsistentRingProvider> CreateServers(int n)
        {
            Dictionary<SiloAddress, ConsistentRingProvider> rings = new Dictionary<SiloAddress, ConsistentRingProvider>();

            for (int i = 1; i <= n; i++)
            {
                SiloAddress addr = SiloAddress.NewLocalAddress(i);
                rings.Add(addr, new ConsistentRingProvider(addr));
            }
            return rings;
        }

        private Dictionary<SiloAddress, ConsistentRingProvider> Combine(Dictionary<SiloAddress, ConsistentRingProvider> set1, Dictionary<SiloAddress, ConsistentRingProvider> set2)
        {
            // tell set1 about every node in set2
            foreach (ConsistentRingProvider crp in set1.Values)
            {
                foreach (ConsistentRingProvider other in set2.Values)
                {
                    if (!crp.MyAddress.Equals(other.MyAddress))
                    {
                        other.AddServer(crp.MyAddress);
                    }
                }
            }

            // tell set2 about every node in set1 ... even if set1 and set2 overlap, ConsistentRingProvider should be able to handle
            foreach (ConsistentRingProvider crp in set2.Values)
            {
                foreach (ConsistentRingProvider other in set1.Values)
                {
                    if (!crp.MyAddress.Equals(other.MyAddress))
                    {
                        other.AddServer(crp.MyAddress);
                    }
                }
            }

            // tell set2 about every node in set2 ... note that set1 already knows about nodes in set1
            foreach (ConsistentRingProvider crp in set2.Values)
            {
                foreach (ConsistentRingProvider other in set2.Values)
                {
                    if (!crp.MyAddress.Equals(other.MyAddress))
                    {
                        other.AddServer(crp.MyAddress);
                    }
                }
            }

            // merge the sets
            return set1.Union(set2).ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        private void RemoveServers(Dictionary<SiloAddress, ConsistentRingProvider> rings, List<SiloAddress> remove)
        {
            foreach (SiloAddress addr in remove)
            {
                rings.Remove(addr);
            }

            // tell every node about the failed nodes
            foreach (ConsistentRingProvider crp in rings.Values)
            {
                foreach (SiloAddress addr in remove)
                {
                    crp.RemoveServer(addr);
                }
            }
        }

        private void VerifyRing(Dictionary<SiloAddress, ConsistentRingProvider> rings)
        {
            RangeBreakable fullring = new RangeBreakable();

            foreach (ConsistentRingProvider r in rings.Values)
            {
                // see if there is no overlap between the responsibilities of two nodes
                Assert.IsTrue(fullring.Remove(r.GetMyRange()), string.Format("Couldn't find & break range {0} in {1}. Some other node already claimed responsibility.", r.GetMyRange(), fullring));
            }
            Assert.IsTrue(fullring.NumRanges == 0, string.Format("Range not completely covered. Uncovered ranges: {0}", fullring));
        }

        /// <summary>
        /// Compare SiloAddress-es based on their consistent hash code
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        private static int CompareSiloAddressesByHash(SiloAddress x, SiloAddress y)
        {
            if (x == null)
            {
                return y == null ? 0 : -1;
            }
            else
            {
                if (y == null)
                {
                    return 1;
                }
                else
                {
                    // real comparison is here
                    return x.GetConsistentHashCode().CompareTo(y.GetConsistentHashCode());
                }
            }
        }

        #endregion
    }

    internal class RangeBreakable
    {
        private List<SingleRange> ranges { get; set; }
        internal int NumRanges { get { return ranges.Count(); } }

        public RangeBreakable()
        {
            ranges = new List<SingleRange>(1);
            ranges.Add(new SingleRange(0, 0));
        }

        public bool Remove(IRingRange range)
        {
            bool wholerange = true;
            foreach (SingleRange s in RangeFactory.GetSubRanges(range))
            {
                bool found = false;
                foreach (SingleRange m in ranges)
                {
                    if (m.Begin == m.End) // treat full range as special case
                    {
                        found = true;
                        ranges.Remove(m);
                        if (s.Begin != s.End) // if s is full range as well, then end of story ... whole range is covered
                        {
                            ranges.Add(new SingleRange(s.End, s.Begin));
                        }
                        break;
                    }

                    if (m.InRange(s.Begin + 1) && m.InRange(s.End)) // s cant overlap two singleranges
                    {
                        found = true;
                        ranges.Remove(m);
                        if (s.Begin != m.Begin)
                        {
                            ranges.Add(new SingleRange(m.Begin, s.Begin));
                        }
                        if (s.End != m.End)
                        {
                            ranges.Add(new SingleRange(s.End, m.End));
                        }
                        break;
                    }
                }
                wholerange = wholerange && found;
            }
            return wholerange;
        }
    }
}
