using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using LoadTestGrainInterfaces;
using Orleans.Placement;
using Orleans.Runtime;

namespace LoadTestGrains
{
    public abstract class BenchmarkLoadGrain_Base : Grain
    {
        public override Task OnActivateAsync()
        {
            return TaskDone.Done;
        }

        // just an empty dummy method to cause activation.
        public Task Initialize()
        {
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public Task PingImmutable(Immutable<byte[]> data)
        {
            return TaskDone.Done;
        }

        public async Task PingImmutableWithDelay(Immutable<byte[]> data, TimeSpan delay)
        {
            //await Task.Delay(delay).WithTimeout(TimeSpan.FromSeconds(1));
            await Task.Delay(delay);
        }

        public Task PingMutableArray_TwoHop(byte[] data, Guid nextGrain, BenchmarkGrainType type)
        {
            if (nextGrain != Guid.Empty)
            {
                return GrainSelector.GetGrain(type, nextGrain).PingMutableArray_TwoHop(data, Guid.Empty, type);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableArray_TwoHop(Immutable<byte[]> data, Guid nextGrain, BenchmarkGrainType type)
        {
            if (nextGrain != Guid.Empty)
            {
                return GrainSelector.GetGrain(type, nextGrain).PingImmutableArray_TwoHop(data, Guid.Empty, type);
            }
            return TaskDone.Done;
        }

        public Task PingMutableDictionary_TwoHop(Dictionary<int, string> data, Guid nextGrain, BenchmarkGrainType type)
        {
            if (nextGrain != Guid.Empty)
            {
                return GrainSelector.GetGrain(type, nextGrain).PingMutableDictionary_TwoHop(data, Guid.Empty, type);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableDictionary_TwoHop(Immutable<Dictionary<int, string>> data, Guid nextGrain, BenchmarkGrainType type)
        {
            if (nextGrain != Guid.Empty)
            {
                return GrainSelector.GetGrain(type, nextGrain).PingImmutableDictionary_TwoHop(data, Guid.Empty, type);
            }
            return TaskDone.Done;
        }

        public Task RandomWalk(Immutable<byte[]> data, Guid[] walk, int position, BenchmarkGrainType type)
        {
            position++;
            if (position < walk.Length)
            {
                return GrainSelector.GetGrain(type, walk[position]).RandomWalk(data, walk, position, type);
            }
            return TaskDone.Done;
        }

        public Task PingSessionToPlayer(Immutable<byte[]> data, Guid[] players, bool isSession, BenchmarkGrainType type)
        {
            if (isSession)
            {
                List<Task> tasks = new List<Task>();
                for (int i = 0; i < players.Length; i++)
                {
                    tasks.Add(GrainSelector.GetGrain(type, players[i]).PingSessionToPlayer(data, players, false, type));
                }
                return Task.WhenAll(tasks);
            }
            return TaskDone.Done;
        }
    }

    public class RandomNonReentrantBenchmarkLoadGrain : BenchmarkLoadGrain_Base, IRandomNonReentrantBenchmarkLoadGrain
    {
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = base.GetLogger("RandomNonReentrantBenchmarkLoadGrain " + base.RuntimeIdentity);
            _logger.Info(1, "OnActivateAsync");
            return TaskDone.Done;
        }
    }

    [Reentrant]
    public class RandomReentrantBenchmarkLoadGrain : BenchmarkLoadGrain_Base, IRandomReentrantBenchmarkLoadGrain
    {
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = base.GetLogger("RandomReentrantBenchmarkLoadGrain " + base.RuntimeIdentity);
            _logger.Info(1, "OnActivateAsync");
            return TaskDone.Done;
        }
    }

    [PreferLocalPlacement]
    public class LocalNonReentrantBenchmarkLoadGrain : BenchmarkLoadGrain_Base, ILocalNonReentrantBenchmarkLoadGrain
    {
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = base.GetLogger("LocalNonReentrantBenchmarkLoadGrain " + base.RuntimeIdentity);
            _logger.Info(1, "OnActivateAsync");
            return TaskDone.Done;
        }
    }

    [Reentrant]
    [PreferLocalPlacement]
    public class LocalReentrantBenchmarkLoadGrain : BenchmarkLoadGrain_Base, ILocalReentrantBenchmarkLoadGrain
    {
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = base.GetLogger("LocalReentrantBenchmarkLoadGrain " + base.RuntimeIdentity);
            _logger.Info(1, "OnActivateAsync");
            return TaskDone.Done;
        }
    }

    /*
    [Reentrant]
    public class ReentrantGraphPartitionBenchmarkLoadGrain : BenchmarkLoadGrain_Base, IReentrantGraphPartitionBenchmarkLoadGrain
    {
        private Logger _logger;

        public override Task OnActivateAsync()
        {
            _logger = base.GetLogger("ReentrantBenchmarkLoadGrain " + base.RuntimeIdentity);
            _logger.Info(1, "OnActivateAsync");
            return TaskDone.Done;
    }
    }*/

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
            return null;
        }
    }
}
