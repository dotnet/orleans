using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime.Placement;
using Orleans;
using Orleans.Runtime;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Orleans.Runtime.Configuration;

namespace UnitTests.Elasticity
{
    internal class DummyContext : IPlacementContext
    {
        public ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics> SiloStatistics = new ConcurrentDictionary<SiloAddress, SiloRuntimeStatistics>();
        public SiloAddress Primary { get; set; }

        static int siloCount = 0;

        public DummyContext()
        {
        }

        public SiloAddress addSilo()
        {
            SiloAddress silo = SiloAddress.NewLocalAddress(siloCount++);
            SiloRuntimeStatistics stats = new SiloRuntimeStatistics();
            SiloStatistics[silo] = stats;

            if (Primary == null || AllSilos.Count == 0)
            {
                Primary = silo;
            }

            return silo;
        }

        public TraceLogger Logger
        {
            get { throw new NotImplementedException(); }
        }

        public bool FastLookup(GrainId grain, out List<ActivationAddress> addresses)
        {
            throw new NotImplementedException();
        }

        public Task<List<ActivationAddress>> FullLookup(GrainId grain)
        {
            throw new NotImplementedException();
        }

        public bool LocalLookup(GrainId grain, out List<ActivationData> addresses)
        {
            throw new NotImplementedException();
        }

        public List<SiloAddress> AllSilos
        {
            get { return SiloStatistics.Keys.ToList(); }
        }

        public SiloAddress LocalSilo
        {
            get { return Primary; }
        }

        public bool TryGetActivationData(ActivationId id, out ActivationData activationData)
        {
            throw new NotImplementedException();
        }

        public void GetGrainTypeInfo(int typeCode, out string grainClass, out PlacementStrategy placement, string genericArguments = null)
        {
            grainClass = "DummyGrain";
            placement = ActivationCountBasedPlacement.Singleton;
        }
    }

    [TestClass]
    public class ElasticPlacementDummyContextTests
    {
        private DummyContext context;
        private ActivationCountPlacementDirector director;
        private PlacementStrategy strategy;

        private ConcurrentBag<PlacementResult> placements;


        private SiloAddress StartAdditionalOrleans(int amount = 0, float cpuUsage = 0.0f, float availableMemory = 2048.0f,
                                                   long totalPhysicalMemory = 4096, bool isOverloaded = false, 
                                                   bool notifyDirector = true)
        {
            var s = context.addSilo();
            context.SiloStatistics[s].IsOverloaded = isOverloaded;
            context.SiloStatistics[s].CpuUsage = cpuUsage;
            context.SiloStatistics[s].ActivationCount = amount;
            context.SiloStatistics[s].AvailableMemory = availableMemory;
            context.SiloStatistics[s].TotalPhysicalMemory = totalPhysicalMemory;

            if (notifyDirector) {
                director.SiloStatisticsChangeNotification(s, context.SiloStatistics[s]);
            }
            return s;
        }

        private void StopSilo(SiloAddress key)
        {
            director.RemoveSilo(key);
            SiloRuntimeStatistics ignore;
            context.SiloStatistics.TryRemove(key, out ignore);
        }

        private void StopAllSilos()
        {
            foreach (var s in context.SiloStatistics.Keys)
            {
                director.RemoveSilo(s);
            }
            context.SiloStatistics.Clear();
            
        }

        private Task<PlacementResult> addDummyGrain()
        {
            var targetGrain = GrainId.NewId();
            return director.SelectSilo(strategy, targetGrain, context);
        }

        [TestInitialize()]
        public void Startup()
        {
            ActivationCountBasedPlacement.Initialize();

            strategy = ActivationCountBasedPlacement.Singleton;
            context = new DummyContext();
            director = new ActivationCountPlacementDirector();
            director.Initialize(new GlobalConfiguration());

            placements = new ConcurrentBag<PlacementResult>();
        }

        private List<PlacementResult> addDummyGrains(int amount)
        {
            List<Task<PlacementResult>> actions = new List<Task<PlacementResult>>();
            for (int i = 0; i < amount; i++)
            {
                actions.Add(addDummyGrain());
            }

            return Task.WhenAll(actions).Result.ToList();
        }

        private IDictionary<SiloAddress, int> groupPlacementResults(IList<PlacementResult> results)
        {
            IDictionary<SiloAddress, int> dict = new Dictionary<SiloAddress, int>();
            foreach (var result in results)
            {
                if (!dict.ContainsKey(result.Silo))
                {
                    dict[result.Silo] = 0;
                }
                dict[result.Silo] += 1;
            }

            return dict;
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_TestSimpleAdd()
        {
            StartAdditionalOrleans();

            var placementResult = addDummyGrain().Result;
            Assert.AreEqual(placementResult.Silo, context.Primary);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_CatchingUp()
        {
            StartAdditionalOrleans();

            int PER_SILO = 10000;

            List<PlacementResult> results = addDummyGrains(PER_SILO);
            StartAdditionalOrleans(0);
            results.AddRange(addDummyGrains(PER_SILO));

            var dict = groupPlacementResults(results);

            foreach(var kvp in dict) {
                Assert.AreEqual(kvp.Value, PER_SILO,
                    String.Format("Expected {0} per silo but Silo {1} received {2} activations.", PER_SILO, kvp.Key, kvp.Value));
            }
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_Simple()
        {
            StartAdditionalOrleans(100);
            StartAdditionalOrleans(10);
            StartAdditionalOrleans(0);

            addDummyGrains(1);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_AllSilosZero()
        {
            StartAdditionalOrleans(0);
            StartAdditionalOrleans(0);
            StartAdditionalOrleans(0);
            StartAdditionalOrleans(0);
            StartAdditionalOrleans(0);

            var results = addDummyGrains(5000);
            var dict = groupPlacementResults(results);
            foreach (var kvp in dict)
            {
                AssertIsInRange(kvp.Value, 1000, 100);
            }
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_AllSilosSame()
        {
            StartAdditionalOrleans(1000);
            StartAdditionalOrleans(1000);
            StartAdditionalOrleans(1000);
            StartAdditionalOrleans(1000);
            StartAdditionalOrleans(1000);

            var results = addDummyGrains(5000);
            var dict = groupPlacementResults(results);
            foreach (var kvp in dict)
            {
                AssertIsInRange(kvp.Value, 1000, 100);
            }
        }

        [TestMethod, TestCategory("Elasticity")]
        [ExpectedException(typeof(OrleansException))]
        public void ElasticPlacementDummyContextTests_NoSiloAvailable()
        {
            var results = addDummyGrains(1);
        }

        [TestMethod, TestCategory("Elasticity")]
        [ExpectedException(typeof(OrleansException))]
        public void ElasticPlacementDummyContextTests_AllSilosCpuUsageTooHigh()
        {
            StartAdditionalOrleans(cpuUsage: 110f);
            StartAdditionalOrleans(cpuUsage: 111f);
            var results = addDummyGrains(1);
        }

        [TestMethod, TestCategory("Elasticity")]
        [ExpectedException(typeof(OrleansException))]
        public void ElasticPlacementDummyContextTests_AllSilosOverloaded()
        {
            StartAdditionalOrleans(isOverloaded: true);
            StartAdditionalOrleans(isOverloaded: true);
            var results = addDummyGrains(1);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_NeverPlaceOnOverloadedSilo()
        {
            StartAdditionalOrleans(amount: 1000, cpuUsage: 109f);
            var u1 = StartAdditionalOrleans(cpuUsage: 110f);
            var u2 = StartAdditionalOrleans(isOverloaded: true);

            var results = addDummyGrains(1000);

            foreach (var pl in results) 
            {
                Assert.AreNotEqual(u1, pl.Silo);
                Assert.AreNotEqual(u2, pl.Silo);
            }
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_NeverPlaceOnOfflineSilo()
        {
            StartAdditionalOrleans(amount: 1000, cpuUsage: 109f, availableMemory: 205f);
            var o1 = StartAdditionalOrleans();
            var o2 = StartAdditionalOrleans();
            StopSilo(o1);
            StopSilo(o2);

            var results = addDummyGrains(1000);

            foreach (var pl in results)
            {
                Assert.AreNotEqual(o1, pl.Silo);
                Assert.AreNotEqual(o2, pl.Silo);
            }
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_NegativeActivationCounts()
        {
            var s0 = StartAdditionalOrleans(-1000);
            var s1 = StartAdditionalOrleans(0);
            
            var results = addDummyGrains(10000);
            var dict = groupPlacementResults(results);

            // FYI: After 500 placements, the algorithm assumes the deficit is gone with 2 silos
            AssertIsInRange(dict[s0], 5250, 100);
            AssertIsInRange(dict[s1], 4750, 100);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_NegativeCpuUsage()
        {
            StartAdditionalOrleans(cpuUsage: -1.0f);
            var results = addDummyGrains(1000);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_PowerOfTwoPlacement_66()
        {
            var s1 = StartAdditionalOrleans(10000000);
            var s2 = StartAdditionalOrleans(10000000);
            var s3 = StartAdditionalOrleans(0);

            // 3 Silos = Power Of Two routes 66% of activations to least loaded silo
            var results = addDummyGrains(1000000);
            var dict = groupPlacementResults(results);

            AssertIsInRange(dict[s3], 1000000 * 0.66, 10000);
            StopSilo(s1);
            StopSilo(s2);
            StopSilo(s3);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_PowerOfTwoPlacement_20()
        {
            // 10 Silos = Power Of Two routes 20% of activations to least loaded silo
            for (int i = 0; i < 9; i++)
            {
                StartAdditionalOrleans(10000000);
            }
            var s10 = StartAdditionalOrleans(0);
            var results = addDummyGrains(1000000);
            var dict = groupPlacementResults(results);

            AssertIsInRange(dict[s10], 1000000 * 0.20, 10000);
        }

        [TestMethod, TestCategory("Elasticity")]
        public void ElasticPlacementDummyContextTests_PowerOfThreePlacement()
        {
            var gc = new GlobalConfiguration();
            gc.ActivationCountBasedPlacementChooseOutOf = 3;
            director = new ActivationCountPlacementDirector();
            director.Initialize(gc);

            // 10 Silos = Power Of Three routes 30% of activations to least loaded silo
            for (int i = 0; i < 9; i++)
            {
                StartAdditionalOrleans(10000000);
            }
            var s10 = StartAdditionalOrleans(0);
            var results = addDummyGrains(1000000);
            var dict = groupPlacementResults(results);

            AssertIsInRange(dict[s10], 1000000 * 0.30, 10000);
        }

        private static void AssertIsInRange(int actual, double expected, int leavy)
        {
            Assert.IsTrue(expected - leavy <= actual && actual <= expected + leavy,
                String.Format("Expecting a value in the range between {0} and {1}, but instead got {2} outside the range.",
                    expected - leavy, expected + leavy, actual));
        }

    }

}
