using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using UnitTests.GrainInterfaces;
using Orleans.Runtime.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Internal;
using System.Threading;

namespace UnitTests.Grains
{
    internal class StressTestGrain : Grain, IStressTestGrain
    {
        private string label;

        private readonly ILogger logger;

        public StressTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            if (this.GetPrimaryKeyLong() == -2)
                throw new ArgumentException("Primary key cannot be -2 for this test case");

            this.label = this.GetPrimaryKeyLong().ToString();
            this.logger.LogInformation("OnActivateAsync");

            return Task.CompletedTask;
        }

        public Task<string> GetLabel()
        {
            return Task.FromResult(this.label);
        }

        public Task SetLabel(string label)
        {
            this.label = label;

            //logger.Info("SetLabel {0} received", label);
            return Task.CompletedTask;
        }

        public Task<IStressTestGrain> GetGrainReference()
        {
            return Task.FromResult(this.AsReference<IStressTestGrain>());
        }

        public Task PingOthers(long[] others)
        {
            var promises = new List<Task>();
            foreach (var key in others)
            {
                var g1 = this.GrainFactory.GetGrain<IStressTestGrain>(key);
                Task promise = g1.GetLabel();
                promises.Add(promise);
            }
            return Task.WhenAll(promises);
        }

        public Task<List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>> LookUpMany(
            SiloAddress destination, List<Tuple<GrainId, int>> grainAndETagList, int retries = 0)
        {
            var list = new List<Tuple<GrainId, int, List<Tuple<SiloAddress, ActivationId>>>>();
            foreach (var tuple in grainAndETagList)
            {
                var id = tuple.Item1;
                var reply = new List<Tuple<SiloAddress, ActivationId>>();
                for (var i = 0; i < 10; i++)
                {
                    var siloAddress = SiloAddress.New(new IPEndPoint(ConfigUtilities.GetLocalIPAddress(),0), 0);
                    reply.Add(new Tuple<SiloAddress, ActivationId>(siloAddress, ActivationId.NewId()));
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
            return Task.CompletedTask;
        }

        public async Task PingWithDelay(byte[] data, TimeSpan delay)
        {
            await Task.Delay(delay);
        }

        public Task Send(byte[] data)
        {
            return Task.CompletedTask;
        }

        public Task DeactivateSelf()
        {
            DeactivateOnIdle();
            return Task.CompletedTask;
        }
    }

    [Reentrant]
    internal class ReentrantStressTestGrain : Grain, IReentrantStressTestGrain
    {
        private string label;
        private readonly ILogger logger;

        public ReentrantStressTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.label = this.GetPrimaryKeyLong().ToString();
            this.logger.LogInformation("OnActivateAsync");
            return Task.CompletedTask;
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
            return Task.CompletedTask;
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
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain).PingMutableArray(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableArray(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableArray(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableArray(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingMutableDictionary(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableDictionary(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain,
            bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableDictionary(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableDictionary(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public async Task InterleavingConsistencyTest(int numItems)
        {
            var delay = TimeSpan.FromMilliseconds(1);
            var getFileMetadataPromises = new List<Task>(numItems*2);
            var fileMetadatas = new Dictionary<int, string>(numItems*2);

            for (var i = 0; i < numItems; i++)
            {
                var capture = i;
                Func<Task> func = (
                    async () =>
                    {
                        await Task.Delay(RandomTimeSpan.Next(delay));
                        var fileMetadata = capture;
                        if ((fileMetadata%2) == 0)
                        {
                            fileMetadatas.Add(fileMetadata, fileMetadata.ToString());
                        }
                    });
                getFileMetadataPromises.Add(func());
            }

            await Task.WhenAll(getFileMetadataPromises.ToArray());

            var tagPromises = new List<Task>(fileMetadatas.Count);

            foreach (var keyValuePair in fileMetadatas)
            {
                var fileId = keyValuePair.Key;
                Func<Task> func = (async () =>
                {
                    await Task.Delay(RandomTimeSpan.Next(delay));
                    _ = fileMetadatas[fileId];
                });
                tagPromises.Add(func());
            }

            await Task.WhenAll(tagPromises);

            // sort the fileMetadatas according to fileIds.
            var results = new List<string>(fileMetadatas.Count);
            for (var i = 0; i < numItems; i++)
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
        private readonly ILogger logger;

        public ReentrantLocalStressTestGrain(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger($"{this.GetType().Name}-{this.IdentityString}");
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            this.label = this.GetPrimaryKeyLong().ToString();
            this.logger.LogInformation("OnActivateAsync");
            return Task.CompletedTask;
        }

        public Task<byte[]> Echo(byte[] data)
        {
            return Task.FromResult(data);
        }

        public Task<string> GetRuntimeInstanceId()
        {
            return Task.FromResult(this.RuntimeIdentity);
        }

        public Task Ping(byte[] data)
        {
            return Task.CompletedTask;
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
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain).PingMutableArray(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableArray(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingImmutableArray(Immutable<byte[]> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableArray(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableArray(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingMutableDictionary(Dictionary<int, string> data, long nextGrain, bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingMutableDictionary(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingMutableDictionary(data, -1, false);
            }
            return Task.CompletedTask;
        }

        public Task PingImmutableDictionary(Immutable<Dictionary<int, string>> data, long nextGrain,
            bool nextGrainIsRemote)
        {
            if (nextGrain > 0)
            {
                if (nextGrainIsRemote)
                {
                    return this.GrainFactory.GetGrain<IReentrantStressTestGrain>(nextGrain)
                        .PingImmutableDictionary(data, -1, false);
                }
                return this.GrainFactory.GetGrain<IReentrantLocalStressTestGrain>(nextGrain)
                    .PingImmutableDictionary(data, -1, false);
            }
            return Task.CompletedTask;
        }
    }
}
