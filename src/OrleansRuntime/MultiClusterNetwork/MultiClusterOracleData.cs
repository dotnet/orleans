﻿using System;
using System.Collections.Generic;
using Orleans.MultiCluster;
using System.Linq;

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

        private readonly HashSet<IMultiClusterConfigurationListener> confListeners;

        private readonly Logger logger;
        private readonly IInternalGrainFactory grainFactory;

        internal MultiClusterData Current { get { return localData; } }

        internal MultiClusterOracleData(Logger log, IInternalGrainFactory grainFactory)
        {
            logger = log;
            this.grainFactory = grainFactory;
            localData = new MultiClusterData();
            activeGatewaysByCluster = new Dictionary<string, List<SiloAddress>>();
            confListeners = new HashSet<IMultiClusterConfigurationListener>();
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

        internal bool SubscribeToMultiClusterConfigurationEvents(IMultiClusterConfigurationListener listener)
        {
            if (logger.IsVerbose2)
                logger.Verbose2("SubscribeToMultiClusterConfigurationEvents: {0}", listener);

            lock (confListeners)
            {
                if (confListeners.Contains(listener))
                    return false;

                confListeners.Add(listener);
                return true;
            }
        }


        internal bool UnSubscribeFromMultiClusterConfigurationEvents(IMultiClusterConfigurationListener listener)
        {
            if (logger.IsVerbose3)
                logger.Verbose3("UnSubscribeFromMultiClusterConfigurationEvents: {0}", listener);

            lock (confListeners)
            {
                return confListeners.Remove(listener);
            }
        }

        public MultiClusterData ApplyDataAndNotify(MultiClusterData data)
        {
            if (data.IsEmpty)
                return data;

            MultiClusterData delta;
            MultiClusterData prev = this.localData;

            this.localData = prev.Merge(data, out delta);

            if (logger.IsVerbose2)
                logger.Verbose2("ApplyDataAndNotify: delta {0}", delta);

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

                List<IMultiClusterConfigurationListener> listenersToNotify;
                lock (confListeners)
                {
                    // make a copy under the lock
                    listenersToNotify = confListeners.ToList();
                }
               
                foreach (var listener in listenersToNotify)
                {
                    try
                    {
                        listener.OnMultiClusterConfigurationChange(delta.Configuration);
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
