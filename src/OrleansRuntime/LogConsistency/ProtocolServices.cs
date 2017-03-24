﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.LogConsistency;
using Orleans.MultiCluster;
using Orleans.SystemTargetInterfaces;
using Orleans.GrainDirectory;
using Orleans.Serialization;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.MultiClusterNetwork;

namespace Orleans.Runtime.LogConsistency
{
    /// <summary>
    /// Functionality for use by log view adaptors that run distributed protocols. 
    /// This class allows access to these services to providers that cannot see runtime-internals.
    /// It also stores grain-specific information like the grain reference, and caches 
    /// </summary>
    internal class ProtocolServices : ILogConsistencyProtocolServices
    {
        private static readonly object[] EmptyObjectArray = new object[0];

        // pseudo-configuration to use if there is no actual multicluster network
        private static readonly Interner<string, MultiClusterConfiguration> PseudoMultiClusterConfigurations = new Interner<string, MultiClusterConfiguration>();
        private static readonly Func<string, MultiClusterConfiguration> CreatePseudoConfig =
            clusterId => new MultiClusterConfiguration(
                DateTime.UtcNow,
                new[] { clusterId }.ToList());

        private readonly IMultiClusterOracle multiClusterOracle;

        private readonly Logger log;
        private readonly IInternalGrainFactory grainFactory;
        private readonly Grain grain;   // links to the grain that owns this service object
        private readonly MultiClusterConfiguration pseudoMultiClusterConfiguration;
        
        private readonly GlobalConfiguration globalConfig;

        public ProtocolServices(
            Grain gr,
            Factory<string, Logger> logFactory,
            IMultiClusterRegistrationStrategy strategy,
            SerializationManager serializationManager,
            IInternalGrainFactory grainFactory,
            GlobalConfiguration globalConfig,
            IMultiClusterOracle multiClusterOracle)
        {
            this.grain = gr;
            this.log = logFactory("LogConsistencyProtocolServices");
            this.grainFactory = grainFactory;
            this.RegistrationStrategy = strategy;
            this.SerializationManager = serializationManager;
            this.multiClusterOracle = multiClusterOracle;
            this.globalConfig = globalConfig;

            if (!globalConfig.HasMultiClusterNetwork)
            {
                // we are creating a default multi-cluster configuration containing exactly one cluster, this one.
                this.pseudoMultiClusterConfiguration = PseudoMultiClusterConfigurations.FindOrCreate(
                    this.globalConfig.ClusterId,
                    CreatePseudoConfig);
            }
        }
        
        public IMultiClusterRegistrationStrategy RegistrationStrategy { get; }

        public GrainReference GrainReference => grain.GrainReference;

        public async Task<ILogConsistencyProtocolMessage> SendMessage(ILogConsistencyProtocolMessage payload, string clusterId)
        {

            log?.Verbose3("SendMessage {0}->{1}: {2}", this.globalConfig.ClusterId, clusterId, payload);

            // send the message to ourself if we are the destination cluster
            if (this.globalConfig.ClusterId == clusterId)
            {
                var g = (ILogConsistencyProtocolParticipant)grain;
                // we are on the same scheduler, so we can call the method directly
                return await g.OnProtocolMessageReceived(payload);
            }

            // cannot send to remote instance if there is only one instance
            if (RegistrationStrategy.Equals(GlobalSingleInstanceRegistration.Singleton))
            {
                throw new ProtocolTransportException("cannot send protocol message to remote instance because there is only one global instance");
            }

            if (!this.MultiClusterEnabled)
                throw new ProtocolTransportException("no such cluster");

            if (log != null && log.IsVerbose3)
            {
                var gws = this.multiClusterOracle.GetGateways();
                log.Verbose3("Available Gateways:\n{0}", string.Join("\n", gws.Select((gw) => gw.ToString())));
            }

            var clusterGateway = this.multiClusterOracle.GetRandomClusterGateway(clusterId);

            if (clusterGateway == null)
                throw new ProtocolTransportException("no active gateways found for cluster");

            var repAgent = this.grainFactory.GetSystemTarget<ILogConsistencyProtocolGateway>(Constants.ProtocolGatewayId, clusterGateway);

            // test hook
            var filter = (this.multiClusterOracle as MultiClusterOracle)?.ProtocolMessageFilterForTesting;
            if (filter != null && !filter(payload))
                return null;

            try
            {
                var retMessage = await repAgent.RelayMessage(GrainReference.GrainId, payload);
                return retMessage;
            }
            catch (Exception e)
            {
                throw new ProtocolTransportException("failed sending message to cluster", e);
            }
        }

        /// <inheritdoc />
        public SerializationManager SerializationManager { get; }

        public bool MultiClusterEnabled => this.globalConfig.HasMultiClusterNetwork;
    
        public string MyClusterId
        {
            get
            {
                return this.globalConfig.ClusterId;
            }
        }

        private string ClusterDisplayName => this.MultiClusterEnabled ? " " + this.MyClusterId : string.Empty;

        public MultiClusterConfiguration MultiClusterConfiguration
        {
            get
            {
                return this.pseudoMultiClusterConfiguration ?? this.multiClusterOracle.GetMultiClusterConfiguration();
            }
        }

        public IEnumerable<string> GetRemoteInstances()
        {
            if (this.MultiClusterEnabled
                && RegistrationStrategy != ClusterLocalRegistration.Singleton)
            {
                foreach (var cluster in this.multiClusterOracle.GetMultiClusterConfiguration().Clusters)
                {
                    if (cluster != this.globalConfig.ClusterId)
                        yield return cluster;
                }
            }
        }

        public void SubscribeToMultiClusterConfigurationChanges()
        {
            if (this.MultiClusterEnabled)
            {
                // subscribe this grain to configuration change events
                this.multiClusterOracle.SubscribeToMultiClusterConfigurationEvents(GrainReference);
            }
        }

        public void UnsubscribeFromMultiClusterConfigurationChanges()
        {
            if (this.MultiClusterEnabled)
            {
                // unsubscribe this grain from configuration change events
                this.multiClusterOracle.UnSubscribeFromMultiClusterConfigurationEvents(GrainReference);
            }
        }

        public IEnumerable<string> ActiveClusters
        {
            get
            {
                if (!this.MultiClusterEnabled)
                    return this.pseudoMultiClusterConfiguration.Clusters;
                else
                    return this.multiClusterOracle.GetActiveClusters();
            }
        }

        public void ProtocolError(string msg, bool throwexception)
        {

            log?.Error((int)(throwexception ? ErrorCode.LogConsistency_ProtocolFatalError : ErrorCode.LogConsistency_ProtocolError),
                string.Format("{0}{1} Protocol Error: {2}",
                    grain.GrainReference,
                    this.ClusterDisplayName,
                    msg));

            if (!throwexception)
                return;

            if (!this.MultiClusterEnabled)
                throw new OrleansException(string.Format("{0} (grain={1})", msg, grain.GrainReference));
            else
                throw new OrleansException(string.Format("{0} (grain={1}, cluster={2})", msg, grain.GrainReference, this.globalConfig.ClusterId));
        }

        public void CaughtException(string where, Exception e)
        {
            log?.Error((int)ErrorCode.LogConsistency_CaughtException,
               string.Format("{0}{1} Exception Caught at {2}",
                   grain.GrainReference,
                   this.ClusterDisplayName,
                   where),e);
        }

        public void CaughtUserCodeException(string callback, string where, Exception e)
        {
            log?.Warn((int)ErrorCode.LogConsistency_UserCodeException,
                string.Format("{0}{1} Exception caught in user code for {2}, called from {3}",
                   grain.GrainReference,
                   this.ClusterDisplayName,
                   callback,
                   where), e);
        }

        public void Log(Severity severity, string format, params object[] args)
        {
            if (log != null && log.SeverityLevel >= severity)
            {
                var msg = string.Format("{0}{1} {2}",
                        grain.GrainReference,
                        this.ClusterDisplayName,
                        string.Format(format, args));
                log.Log(0, severity, msg, EmptyObjectArray, null);
            }
        }
    }

}
