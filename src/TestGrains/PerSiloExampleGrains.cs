/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Concurrency;
using Orleans.Providers;
using Orleans.Runtime;
using TestGrainInterfaces;

namespace TestGrains
{
    public interface PartitionManagerConfig : IGrainState
    {
        IDictionary<Guid, IPartitionGrain> Partitions { get; set; }
        IDictionary<Guid, PartitionInfo> PartitionInfos { get; set; }
    }

    /// <summary>
    /// Grain implemention for Partition Grains.
    /// One partition grain is created per silo.
    /// </summary>
    [StatelessWorker(1)]
    public class PartitionGrain : Grain, IPartitionGrain
    {
        PartitionInfo PartitionConfig { get; set; }

        private Logger logger;
        private Guid partitionId;
        private string siloId;
        private IPartitionGrain me;
        private IPartitionManager partitionManager;

        public override async Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + "-" + this.GetPrimaryKey());
            logger.Info("Activate");

            partitionId = this.GetPrimaryKey();
            siloId = RuntimeIdentity;
            me = this.AsReference<IPartitionGrain>();
            partitionManager = GrainFactory.GetGrain<IPartitionManager>(0);

            if (PartitionConfig == null)
            {
                PartitionConfig = new PartitionInfo
                {
                    PartitionId = partitionId,
                    SiloId = siloId
                };
            }

            logger.Info("Registering partition grain {0} on silo {1} with partition manager", partitionId, siloId);
            await partitionManager.RegisterPartition(PartitionConfig, me);
            logger.Info("Partition grain {0} has been activated on this silo", partitionId);
        }

        public override async Task OnDeactivateAsync()
        {
            logger.Info("Unregistering partition grain {0} on silo {1} with partition manager", partitionId, siloId);
            await partitionManager.RemovePartition(PartitionConfig, me);
            logger.Info("Partition grain {0} has been stopped on this silo", partitionId);
        }

        public Task<PartitionInfo> Start()
        {
            logger.Info("Partition grain {0} has been started on this silo", partitionId);
            return Task.FromResult(PartitionConfig);
        }

        public Task<PartitionInfo> GetPartitionInfo()
        {
            logger.Info("GetPartitionInfo");
            return Task.FromResult(PartitionConfig);
        }
    }

    /// <summary>
    /// Grain implemention for Partition Manager Grain.
    /// One partition manager grain is created per cluster.
    /// By convention, only Id=0 will be used.
    /// </summary>
    [StorageProvider(ProviderName = "PartitionManagerStore")] // Note: Using MemoryStore for testing only.
    [Reentrant]
    public class PartitionManagerGrain : Grain<PartitionManagerConfig>, IPartitionManager
    {
        private Logger logger;

        public override Task OnActivateAsync()
        {
            logger = GetLogger(GetType().Name + this.GetPrimaryKey());
            logger.Info("Activate");

            if (State.PartitionInfos == null)
            {
                State.PartitionInfos = new Dictionary<Guid, PartitionInfo>();
            }
            if (State.Partitions == null)
            {
                State.Partitions = new Dictionary<Guid, IPartitionGrain>();
            }
            // Don't need to write default init data back to store
            return TaskDone.Done;
        }

        public Task<IList<IPartitionGrain>> GetPartitions()
        {
            IList<IPartitionGrain> partitions = State.Partitions.Values.ToList();
            return Task.FromResult(partitions);
        }

        public Task<IList<PartitionInfo>> GetPartitionInfos()
        {
            IList<PartitionInfo> partitionInfos = State.PartitionInfos.Values.ToList();
            return Task.FromResult(partitionInfos);
        }

        public async Task RegisterPartition(PartitionInfo partitionInfo, IPartitionGrain partitionGrain)
        {
            logger.Info("RegisterPartition {0} on silo {1}", partitionInfo.PartitionId, partitionInfo.SiloId);
            Guid partitionId = partitionInfo.PartitionId;
            State.Partitions[partitionId] = partitionGrain;
            State.PartitionInfos[partitionId] = partitionInfo;
            await WriteStateAsync();
        }

        public async Task RemovePartition(PartitionInfo partitionInfo, IPartitionGrain partitionGrain)
        {
            logger.Info("RemovePartition {0} on silo {1}", partitionInfo.PartitionId, partitionInfo.SiloId);
            Guid partitionId = partitionInfo.PartitionId;
            State.Partitions.Remove(partitionId);
            State.PartitionInfos.Remove(partitionId);
            await WriteStateAsync();
        }

        public async Task Broadcast(Func<IPartitionGrain, Task> asyncAction)
        {
            IDictionary<Guid, IPartitionGrain> partitions = State.Partitions;
            logger.Info("Broadcast: Send to {0} partitions", partitions.Count);
            var tasks = new List<Task>();
            foreach (var p in partitions)
            {
                Guid id = p.Key;
                IPartitionGrain grain = p.Value;
                logger.Info("Broadcast: Sending message to partition {0} on silo {1}", id, State.PartitionInfos[id].SiloId);
                tasks.Add(asyncAction(grain));
            }
            // Await here so that tail async is contained and any errors show this method in stack trace.
            logger.Info("Broadcast: Awaiting ack from {0} partitions", tasks.Count);
            await Task.WhenAll(tasks);
        }

    }

    /// <summary>
    /// App startup shim to bootstrap the partition grain in each silo.
    /// </summary>
    public class PartitionStartup : IBootstrapProvider
    {
        public string Name { get; private set; }
        public Guid PartitionId { get; private set; }
        public string HostId { get; private set; }

        private IGrainFactory GrainFactory { get; set; }
        private Logger logger;

        /// <summary>
        /// Bootstrap the partition grain in this silo.
        /// </summary>
        public async Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            logger = providerRuntime.GetLogger("Startup:" + name);
            GrainFactory = providerRuntime.GrainFactory;

            HostId = providerRuntime.SiloIdentity;

            // Use different partition id value for each silo instance.
            PartitionId = Guid.NewGuid();

            Name = "Partition-" + PartitionId;

            await StartPartition(PartitionId, HostId);
        }

        /// <summary>
        /// Start the partition instance for this silo.
        /// </summary>
        /// <param name="partitionId">Id value for this partition.</param>
        /// <param name="hostId">Id value for this host.</param>
        /// <returns>Async Task to indicate when the partition startup operation is complete.</returns>
        private async Task StartPartition(Guid partitionId, string hostId)
        {
            logger.Info("Creating partition grain id {0} on silo id {1}", partitionId, hostId);
            IPartitionGrain partitionGrain = GrainFactory.GetGrain<IPartitionGrain>(partitionId);
            PartitionInfo partitionInfo = await partitionGrain.Start();
            logger.Info("Partition grain {0} has been started on silo id {1}", partitionId, partitionInfo);
        }
    }
}
