using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.SqlDatabase.ElasticScale.ShardManagement;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;
using Orleans.SqlUtils.StorageProvider.GrainClasses;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;
using Orleans.SqlUtils.StorageProvider.Instrumentation;

namespace Orleans.SqlUtils.StorageProvider.Tests
{
    [TestClass]
    public class ShardTests
    {
        private readonly string ConnectionString = ConfigurationManager.AppSettings["ConnectionString"];
        private readonly string ShardCredentials = ConfigurationManager.AppSettings["ShardCredentials"];

        private const string ShardMap1 = "ShardMap1";
        private const string ShardMap2 = "ShardMap2";
        private const string ShardMap4 = "ShardMap4";
        private const string ShardMap8 = "ShardMap8";

        private const string ShardMapDefault = ShardMap1;
        private Logger logger = TraceLogger.GetLogger("SqlDataManager");

        private GrainStateMap CreateGrainStateMap()
        {
            return new SampleGrainStateMapFactory().CreateGrainStateMap();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void Upsert10KStates()
        {
            InstrumentationContext.Reset();
            const int count = 50000;
            BatchingOptions batchingOptions = new BatchingOptions()
            {
                MaxConcurrentWrites = 1
            };
            Upsert10KStates(ShardMap1, count, batchingOptions);
            //Upsert10KStates(ShardMap4, count, batchingOptions);
            //Upsert10KStates(ShardMap2, count);
            //Upsert10KStates(ShardMap4, count);
            //Upsert10KStates(ShardMap8, count);
        }

        private void Upsert10KStates(string mapName, int count, BatchingOptions batchingOptions)
        {
            //InstrumentationContext.Reset();
            GrainStateMap grainStateMap = CreateGrainStateMap();
            using (var dataManager = new SqlDataManager(logger, grainStateMap, ConnectionString, ShardCredentials, mapName, batchingOptions))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < count; ++i)
                {
                    var state = CreateState(i);
                    tasks.Add(dataManager.UpsertStateAsync(RandomIdentity(), state));
                }
                Task.WaitAll(tasks.ToArray());
                stopwatch.Stop();

                Console.WriteLine(" [{0}] {1} Upserts. {2} max concurrent writes. Elapsed: {3}", mapName, count, batchingOptions.MaxConcurrentWrites, stopwatch.Elapsed);
            }
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void InsertThenUpdate10KStates()
        {
            InstrumentationContext.Reset();
            const int count = 10000;
            var grainStateMap = CreateGrainStateMap();
            var grains = new List<Tuple<GrainIdentity, object>>();
            for (int i = 0; i < count; ++i)
            {
                var state = CreateState(i);
                grains.Add(new Tuple<GrainIdentity, object>(RandomIdentity(), state));
            }

            using (var dataManager = new SqlDataManager(logger, grainStateMap, ConnectionString, ShardCredentials, ShardMapDefault))
            {
                var stopwatch = Stopwatch.StartNew();
                var tasks = grains.Select(grain => dataManager.UpsertStateAsync(grain.Item1, grain.Item2)).ToArray();
                Task.WaitAll(tasks);
                stopwatch.Stop();
                Console.WriteLine(" Insert elapsed: {0}", stopwatch.Elapsed);

                stopwatch = Stopwatch.StartNew();
                tasks = grains.Select(grain => dataManager.UpsertStateAsync(grain.Item1, grain.Item2)).ToArray();
                Task.WaitAll(tasks);
                stopwatch.Stop();
                Console.WriteLine(" Update elapsed: {0}", stopwatch.Elapsed);
            }
        }


        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void WriteThenRead10KStates()
        {
            InstrumentationContext.Reset();
            const int count = 10000;
            var grainStateMap = CreateGrainStateMap();
            var grains = new List<Tuple<GrainIdentity, object>>();
            for (int i = 0; i < count; ++i)
            {
                var state = CreateState(i);
                grains.Add(new Tuple<GrainIdentity, object>(RandomIdentity(), state));
            }

            using (var dataManager = new SqlDataManager(logger, grainStateMap, ConnectionString, ShardCredentials, ShardMapDefault))
            {
                var stopwatch = Stopwatch.StartNew();
                var tasks = grains.Select(grain => dataManager.UpsertStateAsync(grain.Item1, grain.Item2)).ToArray();
                Task.WaitAll(tasks);
                stopwatch.Stop();
                Console.WriteLine(" Insert elapsed: {0}", stopwatch.Elapsed);

                // now read
                stopwatch = Stopwatch.StartNew();
                var rtasks = grains.Select(grain => dataManager.ReadStateAsync(grain.Item1)).ToList();
                Task.WaitAll(rtasks.Cast<Task>().ToArray());
                stopwatch.Stop();
                Console.WriteLine(" Read elapsed: {0}", stopwatch.Elapsed);
            }
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void ReadNonExistentState()
        {
            Task.Run(async () =>
            {
                InstrumentationContext.Reset();
                var grainStateMap = CreateGrainStateMap();
                var grainIdentity = RandomIdentity();

                using (var dataManager = new SqlDataManager(logger, grainStateMap, ConnectionString, ShardCredentials, ShardMapDefault))
                {
                    // now read
                    var stopwatch = Stopwatch.StartNew();
                    var state = await dataManager.ReadStateAsync(grainIdentity);
                    stopwatch.Stop();
                    Console.WriteLine(" Read elapsed: {0}", stopwatch.Elapsed);

                    Assert.IsNull(state); 
                }
            }).Wait();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void OneWriteThenReadState()
        {
            Task.Run(async () =>
            {
                InstrumentationContext.Reset();
                var grainStateMap = CreateGrainStateMap();
                var grainIdentity = RandomIdentity();
                var state = CreateState();

                using (var dataManager = new SqlDataManager(logger, grainStateMap, ConnectionString, ShardCredentials, ShardMapDefault))
                {
                    var stopwatch = Stopwatch.StartNew();
                    await dataManager.UpsertStateAsync(grainIdentity, state);
                    stopwatch.Stop();
                    Console.WriteLine(" Insert elapsed: {0}", stopwatch.Elapsed);

                    // now read
                    stopwatch = Stopwatch.StartNew();
                    var state2 = await dataManager.ReadStateAsync(grainIdentity);
                    stopwatch.Stop();
                    Console.WriteLine(" Read elapsed: {0}", stopwatch.Elapsed);
                    Assert.AreEqual(state, state2);
                }
            }).Wait();
        }

        [Ignore]
        [TestMethod, TestCategory("Functional"), TestCategory("SQLAdapter"), TestCategory("Storage")]
        public void LookupTest()
        {
            Func<Range<int>, int, bool> func = (range, key) => range.Low <= key && (key < range.High || range.HighIsMax);

            Range<int> range1 = new Range<int>(int.MinValue, 0);
            Range<int> range2 = new Range<int>(0);

            Assert.IsTrue(func(range1, int.MinValue));
            Assert.IsTrue(func(range1, int.MinValue + 1));
            Assert.IsTrue(func(range1, -1));
            Assert.IsFalse(func(range1, 0));
            Assert.IsFalse(func(range1, 1));
            Assert.IsFalse(func(range1, int.MaxValue));

            Assert.IsFalse(func(range2, int.MinValue));
            Assert.IsFalse(func(range2, int.MinValue + 1));
            Assert.IsFalse(func(range2, -1));
            Assert.IsTrue(func(range2, 0));
            Assert.IsTrue(func(range2, 1));
            Assert.IsTrue(func(range2, int.MaxValue));
        }

        private readonly Random _random = new Random();
        private GrainIdentity RandomIdentity()
        {
            return new GrainIdentity()
            {
                GrainType = "Orleans.SqlUtils.StorageProvider.GrainClasses.CustomerGrain",
                ShardKey = _random.Next()*(_random.Next(2) == 0 ? 1 : -1),
                GrainKey = Guid.NewGuid().ToString()
            };
        }

        private GrainIdentity Shard0Identity()
        {
            return new GrainIdentity()
            {
                ShardKey = -1,
                GrainKey = Guid.NewGuid().ToString()
            };
        }

        private object CreateState(int iter =-1)
        {
            var dt = DateTime.Now;
            var now = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second);

            if (iter == -1)
            {
                return new Dictionary<string, object>()
                {
                    {"CustomerId", _random.Next()},
                    {"FirstName", "FirstName_" + _random.Next()},
                    {"LastName", "LastName_" + _random.Next()},
                    {"NickName", "NickName_" + _random.Next()},
                    {"BirthDate", new DateTime(_random.Next(40) + 1970, _random.Next(12) + 1,  _random.Next(28) + 1)},
                    {"Gender", _random.Next(2)},
                    {"Country", "Country_" + _random.Next()},
                    {"AvatarUrl", "AvatarUrl_" + _random.Next()},
                    {"KudoPoints", _random.Next()},
                    {"Status", _random.Next()},
                    {"LastLogin", now},
                    {"Devices", new List<IDeviceGrain>()},
                };
            }

            return new Dictionary<string, object>()
            {
                {"CustomerId", iter},
                {"FirstName", "FirstName_" + iter},
                {"LastName", "LastName_" + iter},
                {"NickName", "NickName_" + iter},
                {"BirthDate", new DateTime(_random.Next(40) + 1970, _random.Next(12) + 1,  _random.Next(28) + 1)},
                {"Gender", _random.Next(2)},
                {"Country", "Country_" + _random.Next()},
                {"AvatarUrl", "AvatarUrl_" + _random.Next()},
                {"KudoPoints", _random.Next()},
                {"Status", _random.Next()},
                {"LastLogin", now},
                {"Devices", new List<IDeviceGrain>()},
            };
        }
    }
}

