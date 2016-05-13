using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    internal class StressTestGrain : Grain, IStressTestGrain
    {
        private string label;

        private Logger logger;

        public override Task OnActivateAsync()
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            logger = base.GetLogger("StressTestGrain " + base.RuntimeIdentity);
            label = this.GetPrimaryKeyLong().ToString();
            logger.Info("OnActivateAsync");

            return TaskDone.Done;
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;

            //logger.Info("SetLabel {0} received", label);
            return TaskDone.Done;
        }

        public Task<IStressTestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<IStressTestGrain>());
        }

        public Task PingOthers(long[] others)
        {
            List<Task> promises = new List<Task>();
            foreach (long key in others)
            {
                IStressTestGrain g1 = GrainFactory.GetGrain<IStressTestGrain>(key);
                Task promise = g1.GetLabel();
                promises.Add(promise);
            }
            return Task.WhenAll(promises);
        }

        public Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(
            SiloAddress destination, List<Tuple<GrainId, int>> grainAndETagList, int retries = 0)
        {
            var list = new List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>();
            foreach (Tuple<GrainId, int> tuple in grainAndETagList)
            {
                GrainId id = tuple.Item1;
                var reply = new List<Tuple<SiloAddress, ActivationId>>();
                for (int i = 0; i < 10; i++)
                {
                    reply.Add(new Tuple<SiloAddress, ActivationId>(SiloAddress.NewLocalAddress(0), ActivationId.NewId()));
                }
                list.Add(new Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>(id, 3, reply));
            }
            return Task.FromResult(list);
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task Send(byte[] data)
        {
            return TaskDone.Done;
        }

        public Task DeactivateSelf()
        {
            DeactivateOnIdle();
            return TaskDone.Done;
        }
    }

    [Reentrant]
    internal class ReentrantStressTestGrain : Grain, IReentrantStressTestGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            label = this.GetPrimaryKeyLong().ToString();
            logger = base.GetLogger("ReentrantStressTestGrain " + base.Data.Address.ToString());
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain).PingMutableArray(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableArray(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableArray(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableArray(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingMutableDictionary(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableDictionary(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain,
            bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableDictionary(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableDictionary(data, -1, false);
            }
            return TaskDone.Done;
        }

        public async Task InterleavingConsistencyTest(int numItems)
        {
            TimeSpan delay = TimeSpan.FromMilliseconds(1);
            SafeRandom random = new SafeRandom();

            List<Task> getFileMetadataPromises = new List<Task>(numItems*2);
            Dictionary<int, string> fileMetadatas = new Dictionary<int, string>(numItems*2);

            for (int i = 0; i < numItems; i++)
            {
                int capture = i;
                Func<Task> func = (
                    async () =>
                    {
                        await Task.Delay(random.NextTimeSpan(delay));
                        int fileMetadata = capture;
                        if ((fileMetadata%2) == 0)
                        {
                            fileMetadatas.Add(fileMetadata, fileMetadata.ToString());
                        }
                    });
                getFileMetadataPromises.Add(func());
            }

            await Task.WhenAll(getFileMetadataPromises.ToArray());

            List<Task> tagPromises = new List<Task>(fileMetadatas.Count);

            foreach (KeyValuePair<int, string> keyValuePair in fileMetadatas)
            {
                int fileId = keyValuePair.Key;
                Func<Task> func = (async () =>
                {
                    await Task.Delay(random.NextTimeSpan(delay));
                    string fileMetadata = fileMetadatas[fileId];
                });
                tagPromises.Add(func());
            }

            await Task.WhenAll(tagPromises);

            // sort the fileMetadatas according to fileIds.
            List<string> results = new List<string>(fileMetadatas.Count);
            for (int i = 0; i < numItems; i++)
            {
                string metadata;
                if (fileMetadatas.TryGetValue(i, out metadata))
                {
                    results.Add(metadata);
                }
            }

            if (numItems != results.Count)
            {
                //throw new OrleansException(String.Format("numItems != results.Count, {0} != {1}", numItems, results.Count));
            }

        }
    }

    [Reentrant]
    [StatelessWorker]
    public class ReentrantLocalStressTestGrain : Grain, IReentrantLocalStressTestGrain
    {
        private string label;
        private Logger logger;

        public override Task OnActivateAsync()
        {
            label = this.GetPrimaryKeyLong().ToString();
            logger = base.GetLogger("ReentrantLocalStressTestGrain " + base.Data.Address.ToString());
            logger.Info("OnActivateAsync");
            return TaskDone.Done;
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(RuntimeIdentity);
        }

        public Task Ping(byte[] data)
        {
            return TaskDone.Done;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task PingMutableArray(byte[] data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain).PingMutableArray(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableArray(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableArray(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableArray(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingMutableDictionary(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableDictionary(data, -1, false);
            }
            return TaskDone.Done;
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain,
            bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableDictionary(data, -1, false);
                }
                return GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableDictionary(data, -1, false);
            }
            return TaskDone.Done;
        }
    }
}
