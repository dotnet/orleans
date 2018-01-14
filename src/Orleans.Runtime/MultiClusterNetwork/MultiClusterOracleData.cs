using System;
using System.Collections.Generic;
using Orleans.MultiCluster;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MultiClusterNetwork
{
    internal class MultiClusterOracleData
    {
        private volatile MultiClusterData localData;  // immutable, can read without lock

        // for quick access, we precompute available gateways per cluster
        public IReadOnlyDictionary<string, List<SiloAddress>> ActiveGatewaysByCluster
        {
            get { return activeGatewaysByCluster; }
        }
        private volatile IReadOnlyDictionary<string, List<SiloAddress>> activeGatewaysByCluster;

        private readonly HashSet<GrainReference> confListeners;

        private readonly ILogger logger;
        private readonly IInternalGrainFactory grainFactory;

        internal MultiClusterData Current { get { return localData; } }

        internal MultiClusterOracleData(ILogger log, IInternalGrainFactory grainFactory)
        {
            logger = log;
            this.grainFactory = grainFactory;
            localData = new MultiClusterData();
            activeGatewaysByCluster = new Dictionary<string, List<SiloAddress>>();
            confListeners = new HashSet<GrainReference>();
        }

        private void ComputeAvailableGatewaysPerCluster()
        {
            // organize active gateways by cluster
            var gws = new Dictionary<string, List<SiloAddress>>();
            foreach (var g in localData.Gateways)
                if (g.Value.Status == GatewayStatus.Active)
                {
                    List<SiloAddress> list;
                    if (!gws.TryGetValue(g.Value.ClusterId, out list))
                        list = gws[g.Value.ClusterId] = new List<SiloAddress>();
                    list.Add(g.Key);
                }

            activeGatewaysByCluster = gws;
        }

        internal bool SubscribeToMultiClusterConfigurationEvents(GrainReference observer)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace("SubscribeToMultiClusterConfigurationEvents: {0}", observer);

            lock (confListeners)
            {
                if (confListeners.Contains(observer))
                    return false;

                confListeners.Add(observer);
                return true;
            }
        }


        internal bool UnSubscribeFromMultiClusterConfigurationEvents(GrainReference observer)
        {
            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace("UnSubscribeFromMultiClusterConfigurationEvents: {0}", observer);

            lock (confListeners)
            {
                return confListeners.Remove(observer);
            }
        }

        public IMultiClusterGossipData ApplyDataAndNotify(IMultiClusterGossipData data)
        {
            if (data.IsEmpty)
                return data;

            MultiClusterData delta;
            MultiClusterData prev = this.localData;

            this.localData = prev.Merge(data, out delta);

            if (logger.IsEnabled(LogLevel.Trace))
                logger.Trace("ApplyDataAndNotify: delta {0}", delta);

            if (delta.IsEmpty)
                return delta;

            if (delta.Gateways.Count > 0)
            {
                // some gateways have changed
                ComputeAvailableGatewaysPerCluster();
            }

            if (delta.Configuration != null)
            {
                // notify configuration listeners of change

                List<GrainReference> listenersToNotify;
                lock (confListeners)
                {
                    // make a copy under the lock
                    listenersToNotify = confListeners.ToList();
                }
               
                foreach (var listener in listenersToNotify)
                {
                    try
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                            logger.Trace("-NotificationWork: notify IProtocolParticipant {0} of configuration {1}", listener, delta.Configuration);

                        // enqueue conf change event as grain call
                        var g = this.grainFactory.Cast<ILogConsistencyProtocolParticipant>(listener);
                        g.OnMultiClusterConfigurationChange(delta.Configuration).Ignore();
                    }
                    catch (Exception exc)
                    {
                        logger.Error(ErrorCode.MultiClusterNetwork_LocalSubscriberException,
                            String.Format("IProtocolParticipant {0} threw exception processing configuration {1}",
                            listener, delta.Configuration), exc);
                    }
                }
            }


            return delta;
        }
    }
}
