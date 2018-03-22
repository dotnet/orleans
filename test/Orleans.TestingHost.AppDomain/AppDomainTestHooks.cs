using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.MultiCluster;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Orleans.Runtime.Messaging;
using Orleans.Runtime.MultiClusterNetwork;
using Orleans.Runtime.Placement;
using Orleans.Storage;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Test hook functions for white box testing.
    /// NOTE: this class has to and will be removed entirely. This requires the tests that currently rely on it, to assert using different mechanisms, such as with grains.
    /// </summary>
    internal class AppDomainTestHooks : MarshalByRefObject
    {
        private readonly ISiloHost host;

        public AppDomainTestHooks(ISiloHost host)
        {
            this.host = host;
        }

        /// <summary>Find the named storage provider loaded in this silo. </summary>
        internal IStorageProvider GetStorageProvider(string name) => CheckReturnBoundaryReference("storage provider", this.host.Services.GetRequiredServiceByName<IStorageProvider>(name));

        private static T CheckReturnBoundaryReference<T>(string what, T obj) where T : class
        {
            if (obj == null) return null;
            if (obj is MarshalByRefObject || obj is ISerializable)
            {
                // Reference to the provider can safely be passed across app-domain boundary in unit test process
                return obj;
            }
            throw new InvalidOperationException(
                $"Cannot return reference to {what} {TypeUtils.GetFullName(obj.GetType())} if it is not MarshalByRefObject or Serializable");
        }

        public IDictionary<GrainId, IGrainInfo> GetDirectoryForTypeNamesContaining(string expr)
        {
            var x = new Dictionary<GrainId, IGrainInfo>();
            LocalGrainDirectory localGrainDirectory = this.host.Services.GetRequiredService<LocalGrainDirectory>();
            var catalog = this.host.Services.GetRequiredService<Catalog>();
            foreach (var kvp in localGrainDirectory.DirectoryPartition.GetItems())
            {
                if (kvp.Key.IsSystemTarget || kvp.Key.IsClient || !kvp.Key.IsGrain)
                    continue;// Skip system grains, system targets and clients
                if (catalog.GetGrainTypeName(kvp.Key).Contains(expr))
                    x.Add(kvp.Key, kvp.Value);
            }
            return x;
        }
        
        // store silos for which we simulate faulty communication
        // number indicates how many percent of requests are lost
        private ConcurrentDictionary<IPEndPoint, double> simulatedMessageLoss;
        private readonly SafeRandom random = new SafeRandom();

        internal void BlockSiloCommunication(IPEndPoint destination, double lossPercentage)
        {
            if (simulatedMessageLoss == null)
                simulatedMessageLoss = new ConcurrentDictionary<IPEndPoint, double>();

            simulatedMessageLoss[destination] = lossPercentage;

            var mc = this.host.Services.GetRequiredService<MessageCenter>();
            mc.ShouldDrop = ShouldDrop;
        }

        internal void UnblockSiloCommunication()
        {
            var mc = this.host.Services.GetRequiredService<MessageCenter>();
            mc.ShouldDrop = null;
            simulatedMessageLoss.Clear();
        }

        internal Func<ILogConsistencyProtocolMessage,bool> ProtocolMessageFilterForTesting
        {
            get
            {
                var mco = this.host.Services.GetRequiredService<MultiClusterOracle>();
                return mco.ProtocolMessageFilterForTesting;
            }
            set
            {
                var mco = this.host.Services.GetRequiredService<MultiClusterOracle>();
                mco.ProtocolMessageFilterForTesting = value;
            }
        }

        private bool ShouldDrop(Message msg)
        {
            if (simulatedMessageLoss != null)
            {
                double blockedpercentage;
                simulatedMessageLoss.TryGetValue(msg.TargetSilo.Endpoint, out blockedpercentage);
                return (random.NextDouble() * 100 < blockedpercentage);
            }
            else
                return false;
        }
    }
}