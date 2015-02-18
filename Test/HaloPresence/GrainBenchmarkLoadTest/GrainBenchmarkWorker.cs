using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using Orleans;
using Orleans.Runtime;
using Orleans.Concurrency;
using LoadTestBase;
using LoadTestGrainInterfaces;


namespace GrainBenchmarkLoadTest
{
    public enum BenchmarkFunctionType
    {
        Echo,
        Ping,
        PingImmutable,
        PingImmutableWithDelay,
        PingMutableArray_TwoHop,
        PingImmutableArray_TwoHop,
        PingMutableDictionary_TwoHop,
        PingImmutableDictionary_TwoHop,
        RandomWalk,
        PingSessionToPlayer,
        DynamicSessionPlayer
    }

    public class GrainSelector
    {
        public static IBenchmarkLoadGrain GetGrain(BenchmarkGrainType type, Guid id)
        {
            if (type == BenchmarkGrainType.RandomNonReentrant)
            {
                return RandomNonReentrantBenchmarkLoadGrainFactory.GetGrain(id);
            }
            else if (type == BenchmarkGrainType.RandomReentrant)
            {
                return RandomReentrantBenchmarkLoadGrainFactory.GetGrain(id);
            }
            else if (type == BenchmarkGrainType.LocalNonReentrant)
            {
                return LocalNonReentrantBenchmarkLoadGrainFactory.GetGrain(id);
            }
            else if (type == BenchmarkGrainType.LocalReentrant)
            {
                return LocalReentrantBenchmarkLoadGrainFactory.GetGrain(id);
            }
            else if (type == BenchmarkGrainType.GraphPartitionReentrant)
            {
                //return ReentrantGraphPartitionBenchmarkLoadGrainFactory.GetGrain(id);
            }
            throw new NotSupportedException(type.ToString());
        }
    }

    public class GrainBenchmarkWorker : OrleansClientWorkerBase
    {
        private int nGrains;
        private TimeSpan requestLatency;
        private BenchmarkGrainType grainType;
        private BenchmarkGrainType nextGrainType;
        private BenchmarkFunctionType functionType;
        private int startPoint;
        private byte[] array;
        private Dictionary<int, string> dictionary;
        private List<IBenchmarkLoadGrain> grains;
        private List<Guid> nextHopGrains;
        private GrainBenchmarkGraph graph;


        // player session declarations
        private static DynamicGraph dynamicPlayerSession; 
        private static Thread dynamicPlayerSessionThread;

        // This is an example of worker initialization.
        // Pre-create grains, per-allocate data buffers, etc...
        public void ApplicationInitialize(int numGrains, int dataSize, TimeSpan requestLatency, 
            BenchmarkGrainType grainType, BenchmarkGrainType nextGrainType, BenchmarkFunctionType functionType, string graphFileName = null)
        {
            Random rand = new Random();
            this.nGrains = (numGrains > nRequests) ? (int)nRequests : numGrains;
            this.requestLatency = requestLatency;
            this.grainType = grainType;
            this.nextGrainType = nextGrainType;
            this.functionType = functionType;
            this.startPoint = rand.Next(numGrains);
            this.array = new byte[dataSize];
            this.dictionary = new Dictionary<int, string>();
            this.graph = null;
            for (int i = 0; i < dataSize; i++)
            {
                dictionary[i] = "CONSTANT_DATA_XXX_YYY"; // this.array.Select((byte b) => (int)b).ToList();
            }

            grains = new List<IBenchmarkLoadGrain>();
            nextHopGrains = new List<Guid>();

            if (functionType == BenchmarkFunctionType.DynamicSessionPlayer)
            {
                // player session declaration
                dynamicPlayerSession = new DynamicGraph(-600, 4000, 100 * 1000, 25);
                dynamicPlayerSessionThread = new Thread(dynamicPlayerSession.run);
                dynamicPlayerSessionThread.Start();

                // important test to check that all clients are in sync in the logs
                // each client maintains their own state for players and sessions, they should all be roughly the same
                WriteProgress("started dynamic session thread");
            }
            else
            {
                for (long i = 0; i < nGrains; i++)
                {
                    IBenchmarkLoadGrain grain;
                    if (IsGraphFunction(functionType))
                    {
                        // for graph workloads, we want all clients to have same grains
                        grain = GrainSelector.GetGrain(grainType, GrainBenchmarkGraph.LongToGuid(i));
                    }
                    else
                    {
                        // for non-graph workloads, we want all clients to have different grains
                        // This is importand when we have more than one sillo and want to test local grains.
                        // We want true local grain placement, otherwise since different clients connect via different gateways some msgs will not be local.
                        grain = GrainSelector.GetGrain(grainType, Guid.NewGuid());
                    }

                    grains.Add(grain);
                }
            }

            if (IsTwoHopFunction(functionType))
            {
                nextHopGrains = new List<Guid>();
                for (int i = 0; i < nGrains; i++)
                {
                    nextHopGrains.Add(Guid.NewGuid());
                }
            }

            if (IsGraphFunction(functionType))
            {
                graph = GrainBenchmarkGraph.CreateFileGraph(grains, GrainBenchmarkDriver.INPUT_GRAPH_FILE);
            }

            WarmupGrains();

            Thread.Sleep(TimeSpan.FromSeconds(30));

            WriteProgress("Done ApplicationInitialize by worker " + Name);
        }

        private static bool IsGraphFunction(BenchmarkFunctionType functionType)
        {
            return functionType == BenchmarkFunctionType.RandomWalk ||
                functionType == BenchmarkFunctionType.PingSessionToPlayer;
        }

        private static bool IsTwoHopFunction(BenchmarkFunctionType functionType)
        {
            return  functionType == BenchmarkFunctionType.PingMutableArray_TwoHop ||
                    functionType == BenchmarkFunctionType.PingImmutableArray_TwoHop ||
                    functionType == BenchmarkFunctionType.PingMutableDictionary_TwoHop ||
                    functionType == BenchmarkFunctionType.PingImmutableDictionary_TwoHop;        
        }

        private void WarmupGrains()
        {
            // First hop grain - warm them up always.
            List<IBenchmarkLoadGrain> grainsToWarmup = new List<IBenchmarkLoadGrain>();
            grainsToWarmup.AddRange(grains);
            WriteProgress(1, "Picking up grains to warm up. So far has {0} grains", grainsToWarmup.Count);
            Warmup2ndHopGrains(grainsToWarmup);

            WriteProgress(2, "About to start phase one of warming up " + grainsToWarmup.Count + " grains.");
            DoActualWarmup(grainsToWarmup);
            WriteProgress(3, "Done phase one of warming up " + grainsToWarmup.Count + " grains.");
            DoActualWarmup(grainsToWarmup);
            WriteProgress(4, "Done phase two of warming up " + grainsToWarmup.Count + " grains.");
        }

        private void DoActualWarmup(List<IBenchmarkLoadGrain> grainsToWarmup)
        {
            AsyncPipeline initPipeline = new AsyncPipeline(50);
            List<Task> initPromises = new List<Task>();
            Random rng = new Random();
            int _startPoint = rng.Next(grainsToWarmup.Count);
            for (int i = 0; i < grainsToWarmup.Count; i++)
            {
                Task task = grainsToWarmup[(i + _startPoint) % grainsToWarmup.Count].Initialize();
                initPromises.Add(task);
                initPipeline.Add(task);
            }
            initPipeline.Wait();
            Task.WhenAll(initPromises).Wait();
        }

        private void Warmup2ndHopGrains(List<IBenchmarkLoadGrain> grainsToWarmup)
        {
            // Second hop grains:
                // For local placement, we do not want to warmup non-first-hop grains, since we want the workload to touch them first
            if (grainType != BenchmarkGrainType.LocalReentrant && grainType != BenchmarkGrainType.LocalNonReentrant)
            {
                // For two-hop functions, we have a list of the non-first-hop grains
                if (IsTwoHopFunction(functionType))
                {
                    foreach (var v in nextHopGrains)
                    {
                        grainsToWarmup.Add(GrainSelector.GetGrain(grainType,v));
                    }
                }

                // For graphs, the non-first-hop grains are found from the GetOtherVertices method
                if (IsGraphFunction(functionType))
                {
                    foreach (var v in graph.GetOtherVertices())
                    {
                        grainsToWarmup.Add(GrainSelector.GetGrain(grainType, v));
                    }
                }
            }

            // player session uses their own grains
            if (functionType == BenchmarkFunctionType.DynamicSessionPlayer)
            {
                foreach (var gamePlayersPair in dynamicPlayerSession.gameToPlayers)
                {
                    grainsToWarmup.Add(GrainSelector.GetGrain(grainType, GrainBenchmarkGraph.LongToGuid(gamePlayersPair.Key)));
                    foreach (var player in gamePlayersPair.Value)
                    {
                        grainsToWarmup.Add(GrainSelector.GetGrain(grainType, GrainBenchmarkGraph.LongToGuid(player)));
                    }
                }
            }
        }

        protected override Task IssueRequest(int requestNumber, int threadNumber)
        {
            int index = (requestNumber + startPoint) % nGrains;
            IBenchmarkLoadGrain grain = null;
            if (!functionType.Equals(BenchmarkFunctionType.DynamicSessionPlayer))
            {
                grain = grains[index];
            }

            if (functionType.Equals(BenchmarkFunctionType.Echo))
            {
                return grain.Echo(array);
            }
            else if (functionType.Equals(BenchmarkFunctionType.Ping))
            {
                return grain.Ping(array);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingImmutable))
            {
                return grain.PingImmutable(array.AsImmutable());
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingImmutableWithDelay))
            {
                return grain.PingImmutableWithDelay(array.AsImmutable(), requestLatency);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingMutableArray_TwoHop))
            {
                return grain.PingMutableArray_TwoHop(array, nextHopGrains[index], nextGrainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingImmutableArray_TwoHop))
            {
                return grain.PingImmutableArray_TwoHop(array.AsImmutable(), nextHopGrains[index], nextGrainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingMutableDictionary_TwoHop))
            {
                return grain.PingMutableDictionary_TwoHop(dictionary, nextHopGrains[index], nextGrainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingImmutableDictionary_TwoHop))
            {
                return grain.PingImmutableDictionary_TwoHop(dictionary.AsImmutable(), nextHopGrains[index], nextGrainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.RandomWalk))
            {
                return grain.RandomWalk(array.AsImmutable(), graph.GetRandomWalk(index,10), 0, grainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.PingSessionToPlayer))
            {
                return grain.PingSessionToPlayer(array.AsImmutable(), graph.GetRandomNeighborsWithDuplicates(graph.GetWeightedRandom(), 10), true, grainType);
            }
            else if (functionType.Equals(BenchmarkFunctionType.DynamicSessionPlayer))
            {
                List<long> players;
                long game;
                dynamicPlayerSession.getGame(out game,out players);
                Guid gameGuid = GrainBenchmarkGraph.LongToGuid(game);
                Guid[] playerGuids = new Guid[players.Count];
                int i = 0;
                foreach (var player in players)
                {
                    playerGuids[i] = GrainBenchmarkGraph.LongToGuid(player);
                    i++;
                }

                /*
                 if you are skeptical about consistent views among clients, enable this and check logs
                if (game % 1000 == 7)
                {
                    string s = game+": ";
                    foreach(var player in playerGuids) {
                        s+=player;
                    }
                    WriteProgress(s);
                }*/

                return GrainSelector.GetGrain(grainType, gameGuid).PingSessionToPlayer(array.AsImmutable(), playerGuids, true, grainType);
            }
            throw new ArgumentException("Non supported GrainType " + grainType + " or function type " + functionType, "grainType");
        }
    }
}