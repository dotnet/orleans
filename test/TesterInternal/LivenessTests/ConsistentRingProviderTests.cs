using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Streams;
using Xunit;
using Xunit.Abstractions;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace UnitTests.LivenessTests
{
    public class ConsistentRingProviderTests : IClassFixture<ConsistentRingProviderTests.Fixture>
    {
        private readonly ITestOutputHelper output;

        public class Fixture
        {
            public Fixture()
            {
                BufferPool.InitGlobalBufferPool(Options.Create(new SiloMessagingOptions()));
            }
        }

        public ConsistentRingProviderTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test1()
        {
            SiloAddress silo1 = SiloAddress.NewLocalAddress(0);
            ConsistentRingProvider ring = new ConsistentRingProvider(silo1, NullLoggerFactory.Instance);
            output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());

            ring.AddServer(SiloAddress.NewLocalAddress(1));
            output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());

            ring.AddServer(SiloAddress.NewLocalAddress(2));
            output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test2()
        {
            SiloAddress silo1 = SiloAddress.NewLocalAddress(0);
            VirtualBucketsRingProvider ring = new VirtualBucketsRingProvider(silo1, NullLoggerFactory.Instance, 30);
            //ring.logger.SetSeverityLevel(Severity.Warning);
            output.WriteLine("\n\n*** Silo1 range: {0}.\n*** The whole ring with 1 silo is:\n{1}\n\n", ring.GetMyRange(), ring.ToString());

            for (int i = 1; i <= 10; i++)
            {
                ring.SiloStatusChangeNotification(SiloAddress.NewLocalAddress(i), SiloStatus.Active);
                var range = RangeFactory.CreateEquallyDividedMultiRange(ring.GetMyRange(), 5);
                output.WriteLine("\n\n*** Silo1 range: {0}. \n*** The whole ring with {1} silos is:\n{2}\n\n", range.ToCompactString(), i + 1, ring.ToString());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test3()
        {
            int NUM_SILOS = 100;
            double NUM_QUEUES = 10024.0;
            int NUM_AGENTS = 4;

            Random random = new Random();
            SiloAddress silo1 = SiloAddress.NewLocalAddress(random.Next(100000));
            VirtualBucketsRingProvider ring = new VirtualBucketsRingProvider(silo1, NullLoggerFactory.Instance, 50);
            //ring.logger.SetSeverityLevel(Severity.Warning);
            
            for (int i = 1; i <= NUM_SILOS - 1; i++)
            {
                ring.SiloStatusChangeNotification(SiloAddress.NewLocalAddress(random.Next(100000)), SiloStatus.Active);
            }
  
            IDictionary<SiloAddress, IRingRangeInternal> siloRanges = ring.GetRanges();
            List<Tuple<SiloAddress, IRingRangeInternal>> sortedSiloRanges =
                siloRanges.Select(kv => new Tuple<SiloAddress, IRingRangeInternal>(kv.Key, kv.Value)).ToList();
            sortedSiloRanges.Sort((t1, t2) => t1.Item2.RangePercentage().CompareTo(t2.Item2.RangePercentage()));

            Dictionary<SiloAddress, List<IRingRangeInternal>> allAgentRanges = new Dictionary<SiloAddress, List<IRingRangeInternal>>();
            foreach (var siloRange in siloRanges)
            {
                var multiRange = RangeFactory.CreateEquallyDividedMultiRange(siloRange.Value, NUM_AGENTS);
                List<IRingRangeInternal> agentRanges = new List<IRingRangeInternal>();
                for(int i=0; i < NUM_AGENTS; i++)
                {
                    IRingRangeInternal agentRange = (IRingRangeInternal)multiRange.GetSubRange(i);
                    agentRanges.Add(agentRange);
                }
                allAgentRanges.Add(siloRange.Key, agentRanges);
            }

            Dictionary<SiloAddress, List<int>> queueHistogram = GetQueueHistogram(allAgentRanges, (int)NUM_QUEUES);
            string str = Utils.EnumerableToString(sortedSiloRanges,
                tuple => String.Format("Silo {0} -> Range {1:0.000}%, {2} queues: {3}", 
                    tuple.Item1,
                    tuple.Item2.RangePercentage(),
                    queueHistogram[tuple.Item1].Sum(),
                    Utils.EnumerableToString(queueHistogram[tuple.Item1])), "\n");

            output.WriteLine("\n\n*** The whole ring with {0} silos is:\n{1}\n\n", NUM_SILOS, str);

            output.WriteLine("Total number of queues is: {0}", queueHistogram.Values.Select(list => list.Sum()).Sum());
            output.WriteLine("Expected average range per silo is: {0:0.00}%, expected #queues per silo is: {1:0.00}, expected #queues per agent is: {2:0.000}.",
                100.0 / NUM_SILOS, NUM_QUEUES / NUM_SILOS, NUM_QUEUES / (NUM_SILOS * NUM_AGENTS));
            output.WriteLine("Min #queues per silo is: {0}, Max #queues per silo is: {1}.",
                queueHistogram.Values.Select(list => list.Sum()).ToList().Min(), queueHistogram.Values.Select(list => list.Sum()).ToList().Max());
        }

        private Dictionary<SiloAddress, List<int>> GetQueueHistogram(Dictionary<SiloAddress, List<IRingRangeInternal>> siloRanges, int totalNumQueues)
        {
            HashRingBasedStreamQueueMapper queueMapper = new HashRingBasedStreamQueueMapper(totalNumQueues, "AzureQueues");
            var allQueues = queueMapper.GetAllQueues();

            Dictionary<SiloAddress, List<int>> queueHistogram = new Dictionary<SiloAddress, List<int>>();
            foreach (var siloRange in siloRanges)
            {
                List<int> agentRanges = new List<int>();
                foreach (IRingRangeInternal agentRange in siloRange.Value)
                {
                    int numQueues = queueMapper.GetQueuesForRange(agentRange).Count();
                    agentRanges.Add(numQueues);
                }
                agentRanges.Sort();
                queueHistogram.Add(siloRange.Key, agentRanges);
            }
            //queueHistogram.Sort((t1, t2) => t1.Item2.CompareTo(t2.Item2));
            return queueHistogram;
        }
    }
}

