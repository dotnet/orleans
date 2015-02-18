using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;

namespace UnitTests.General
{
    //[TestClass]
    public class LoadBalancingTests
    {
        private static int numSilos = 20;
        private static bool debug = false;
        private static readonly SafeRandom random = new SafeRandom();

        public void LoadBalancingTests_VirtualBuckets()
        {
            VirtualBuckets_RunMany(1);
            VirtualBuckets_RunMany(2);
            VirtualBuckets_RunMany(5);
            VirtualBuckets_RunMany(10);
            VirtualBuckets_RunMany(20);
            VirtualBuckets_RunMany(50);
            VirtualBuckets_RunMany(100);
            VirtualBuckets_RunMany(200);
        }

        public void LoadBalancingTests_ClientBalancing()
        {
            ClientBalancing_CreateMany(20);
            ClientBalancing_CreateMany(43);
            ClientBalancing_CreateMany(60);
            ClientBalancing_CreateMany(100);
            ClientBalancing_CreateMany(200);
            ClientBalancing_CreateMany(400);
            ClientBalancing_CreateMany(1000);
            ClientBalancing_CreateMany(2000);
            ClientBalancing_CreateMany(4000);
        }

        public void ClientBalancing_CreateMany(int numClients)
        {
            int numTests = 1000;
            int[] silos = new int[numSilos];
            for (int i = 0; i < numTests; i++)
            {
                int[] test = ClientBalancing_CreateOne(numClients);
                for (int j = 0; j < numSilos; j++)
                {
                    silos[j] += test[j];
                }
            }
            for (int j = 0; j < numSilos; j++)
            {
                silos[j] = silos[j] / numTests; // average
            }

            Console.WriteLine("ClientsPerSilo for {0} clients and {1} silos: {2} ", numClients, numSilos, Utils.EnumerableToString(silos));
        }


        // returns the allocation of clients to silos - a list of number of clients for each silo
        private int[] ClientBalancing_CreateOne(int numClients)
        {
            int[] silos = new int[numSilos];
            for(int i=0; i<numClients; i++)
            {
                int silo = random.Next(numSilos);
                silos[silo]++;
            }
            Array.Sort(silos);
            return silos;
        }

        private void VirtualBuckets_RunMany(int bucketsPerSilo)
        {
            int numTests = 100;
            double min = 0;
            double max = 0;
            double span = 0;
            double av = ((double)int.MaxValue / (double)numSilos) * 2;
            for (int i = 0; i < numTests; i++)
            {
                Tuple<double, double> result = VirtualBuckets_Analyze(bucketsPerSilo);
                min += result.Item1;
                max += result.Item2;
                span += (result.Item2 - result.Item1);
            }
            min = min / numTests;
            max = max / numTests;
            span = span / numTests;
            Console.WriteLine("bucketsPerSilo: {0} MAX BUCKET %: {1:F3} , MIN BUCKET %: {2:F3}, SPAN %: {3:F3}", bucketsPerSilo, max / av, min / av, span / av);
            Console.WriteLine("******************************************************");
        }

        // returns the max and min intervals on the ring.
        private Tuple<double, double> VirtualBuckets_Analyze(int bucketsPerSilo)
        {
            //Console.WriteLine("VirtualBuckets for " + bucketsPerSilo + " buckets Per Silo.");
            List<int> buckets = VirtualBuckets_Create(bucketsPerSilo);
            int[] array = buckets.ToArray();
            Array.Sort(array);
            if (debug)
            {
                Console.WriteLine("\n\nSUM BUCKETS: " + Utils.EnumerableToString<int>(array));
            }
            // int av = array.Sum() / array.Length;
            double min = array.First();
            double max = array.Last();
            double av = ((double)int.MaxValue / (double)numSilos) * 2;
            if (debug)
            {
                Console.WriteLine("\nMAX BUCKET: " + max + " MIN BUCKET: " + min + " AVERAGE BUCKET: " + av);
                Console.WriteLine("MAX BUCKET %: " + max / av + " MIN BUCKET %: " + min / av);
                Console.WriteLine("-----------------------------------------------------");
            }
            return new Tuple<double, double>(min, max);
        }

        // returns the summarized buckets - a list of total interval lenghts on the ring for each silo
        private List<int> VirtualBuckets_Create(int bucketsPerSilo)
        {
#if false
            SortedList<int, SiloAddress> directoryRing = new SortedList<int, SiloAddress>();
            List<SiloAddress> silos = new List<SiloAddress>();
            for (int i = 0; i < numSilos; i++)
            {
                SiloAddress silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), random.Next()); // make sure epoch number is different for each silo
                silos.Add(silo);
                List<int> bucketEdges = silo.GetConsistentHashes(bucketsPerSilo);

                // the exact code copy pasted from LocalGrainDirectory
                foreach (var hash in bucketEdges)
                {
                    lock (directoryRing)
                    {
                        if (directoryRing.ContainsKey(hash))
                        {
                            var other = directoryRing[hash];
                            // If two silos conflict, take the lesser of the two (usually the older one; that is, the lower epoch)
                            if (silo.CompareTo(other) > 0)
                            {
                                continue;
                            }
                        }
                        directoryRing[hash] = silo;
                    }
                }
            }

            int[] array = directoryRing.Keys.ToArray();
            Array.Sort(array);
            if (debug)
            {
                Console.WriteLine("RAW BUCKETS: " + Utils.IEnumerableToString<int>(array));
            }

            List<int> sumBuckets = new List<int>();
            foreach (SiloAddress silo in silos)
            {
                int bucketsSum = 0;
                int prev = int.MinValue;
                foreach (var pair in directoryRing)
                {
                    if (pair.Value.Equals(silo))
                    {
                        int bucket = pair.Key - prev;
                        //Assert.IsTrue(bucket > 0);
                        bucketsSum += bucket;
                    }
                    prev = pair.Key;
                }
                int lastBucket = int.MaxValue - prev;
                bucketsSum += lastBucket;
                sumBuckets.Add(bucketsSum);
            }
            return sumBuckets;
#endif
            return null;
        }
    }
}
