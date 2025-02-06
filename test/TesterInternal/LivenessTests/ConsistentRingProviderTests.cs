using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Configuration;
using Orleans.Runtime.ConsistentRing;
using Orleans.Streams;
using TestExtensions;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.LivenessTests
{
    public class ConsistentRingProviderTests(ITestOutputHelper output)
    {
        private readonly ITestOutputHelper _output = output;

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test1()
        {
            SiloAddress silo1 = SiloAddressUtils.NewLocalSiloAddress(0);
            ConsistentRingProvider ring = new ConsistentRingProvider(silo1, NullLoggerFactory.Instance, new FakeSiloStatusOracle());
            _output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());

            ring.AddServer(SiloAddressUtils.NewLocalSiloAddress(1));
            _output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());

            ring.AddServer(SiloAddressUtils.NewLocalSiloAddress(2));
            _output.WriteLine("Silo1 range: {0}. The whole ring is: {1}", ring.GetMyRange(), ring.ToString());
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test2()
        {
            SiloAddress silo1 = SiloAddressUtils.NewLocalSiloAddress(0);
            VirtualBucketsRingProvider ring = new VirtualBucketsRingProvider(silo1, NullLoggerFactory.Instance, 30, new FakeSiloStatusOracle());
            _output.WriteLine("\n\n*** Silo1 range: {0}.\n*** The whole ring with 1 silo is:\n{1}\n\n", ring.GetMyRange(), ring.ToString());

            for (int i = 1; i <= 10; i++)
            {
                ring.SiloStatusChangeNotification(SiloAddressUtils.NewLocalSiloAddress(i), SiloStatus.Active);
                _output.WriteLine("\n\n*** Silo1 range: {0}.\n*** The whole ring with {1} silos is:\n{2}\n\n", ring.GetMyRange(), i + 1, ring.ToString());
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Liveness"), TestCategory("Ring"), TestCategory("RingStandalone")]
        public void ConsistentRingProvider_Test3()
        {
            int NUM_SILOS = 100;
            double NUM_QUEUES = 10024.0;
            int NUM_AGENTS = 4;

            Random random = new Random();
            SiloAddress silo1 = SiloAddressUtils.NewLocalSiloAddress(random.Next(100000));
            VirtualBucketsRingProvider ring = new VirtualBucketsRingProvider(silo1, NullLoggerFactory.Instance, 50, new FakeSiloStatusOracle());

            for (int i = 1; i <= NUM_SILOS - 1; i++)
            {
                ring.SiloStatusChangeNotification(SiloAddressUtils.NewLocalSiloAddress(random.Next(100000)), SiloStatus.Active);
            }

            var siloRanges = ring.GetRanges();
            var sortedSiloRanges = siloRanges.ToList();
            sortedSiloRanges.Sort((t1, t2) => t1.Item2.RangePercentage().CompareTo(t2.Item2.RangePercentage()));

            var allAgentRanges = new List<(SiloAddress, List<IRingRangeInternal>)>();
            foreach (var siloRange in siloRanges)
            {
                List<IRingRangeInternal> agentRanges = new List<IRingRangeInternal>();
                for (int i = 0; i < NUM_AGENTS; i++)
                {
                    IRingRangeInternal agentRange = (IRingRangeInternal)RangeFactory.GetEquallyDividedSubRange(siloRange.Value, NUM_AGENTS, i);
                    agentRanges.Add(agentRange);
                }
                allAgentRanges.Add((siloRange.Key, agentRanges));
            }

            Dictionary<SiloAddress, List<int>> queueHistogram = GetQueueHistogram(allAgentRanges, (int)NUM_QUEUES);
            string str = Utils.EnumerableToString(sortedSiloRanges,
                tuple => string.Format("Silo {0} -> Range {1:0.000}%, {2} queues: {3}",
                    tuple.Item1,
                    tuple.Item2.RangePercentage(),
                    queueHistogram[tuple.Item1].Sum(),
                    Utils.EnumerableToString(queueHistogram[tuple.Item1])), "\n");

            _output.WriteLine("\n\n*** The whole ring with {0} silos is:\n{1}\n\n", NUM_SILOS, str);

            _output.WriteLine("Total number of queues is: {0}", queueHistogram.Values.Sum(list => list.Sum()));
            _output.WriteLine("Expected average range per silo is: {0:0.00}%, expected #queues per silo is: {1:0.00}, expected #queues per agent is: {2:0.000}.",
                100.0 / NUM_SILOS, NUM_QUEUES / NUM_SILOS, NUM_QUEUES / (NUM_SILOS * NUM_AGENTS));
            _output.WriteLine("Min #queues per silo is: {0}, Max #queues per silo is: {1}.",
                queueHistogram.Values.Min(list => list.Sum()), queueHistogram.Values.Max(list => list.Sum()));
        }

        private static Dictionary<SiloAddress, List<int>> GetQueueHistogram(List<(SiloAddress Key, List<IRingRangeInternal> Value)> siloRanges, int totalNumQueues)
        {
            var options = new HashRingStreamQueueMapperOptions();
            options.TotalQueueCount = totalNumQueues;
            HashRingBasedStreamQueueMapper queueMapper = new HashRingBasedStreamQueueMapper(options, "AzureQueues");
            _ = queueMapper.GetAllQueues();

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

        internal sealed class FakeSiloStatusOracle : ISiloStatusOracle
        {
            private readonly Dictionary<SiloAddress, SiloStatus> _content = [];
            private readonly HashSet<ISiloStatusListener> _subscribers = [];

            public FakeSiloStatusOracle()
            {
                SiloAddress = SiloAddress.New(IPAddress.Loopback, Random.Shared.Next(2000, 40_000), SiloAddress.AllocateNewGeneration());
                _content[SiloAddress] = SiloStatus.Active;
            }

            public SiloStatus CurrentStatus => SiloStatus.Active;

            public string SiloName => "TestSilo";

            public SiloAddress SiloAddress { get; }

            public SiloStatus GetApproximateSiloStatus(SiloAddress siloAddress)
            {
                if (_content.TryGetValue(siloAddress, out var status))
                {
                    return status;
                }
                return SiloStatus.None;
            }

            public Dictionary<SiloAddress, SiloStatus> GetApproximateSiloStatuses(bool onlyActive = false)
            {
                return onlyActive
                    ? new Dictionary<SiloAddress, SiloStatus>(_content.Where(kvp => kvp.Value == SiloStatus.Active))
                    : new Dictionary<SiloAddress, SiloStatus>(_content);
            }

            public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
            {
                _content[siloAddress] = status;
                foreach (var subscriber in _subscribers)
                {
                    subscriber.SiloStatusChangeNotification(siloAddress, status);
                }
            }

            public bool IsDeadSilo(SiloAddress silo) => GetApproximateSiloStatus(silo) == SiloStatus.Dead;

            public bool IsFunctionalDirectory(SiloAddress siloAddress) => !GetApproximateSiloStatus(siloAddress).IsTerminating();

            public bool SubscribeToSiloStatusEvents(ISiloStatusListener observer) => _subscribers.Add(observer);

            public bool TryGetSiloName(SiloAddress siloAddress, out string siloName)
            {
                siloName = "TestSilo";
                return true;
            }

            public bool UnSubscribeFromSiloStatusEvents(ISiloStatusListener observer) => _subscribers.Remove(observer);
            public ImmutableArray<SiloAddress> GetActiveSilos() => [.. GetApproximateSiloStatuses(onlyActive: true).Keys];
        }
    }
}

