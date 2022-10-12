using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;
using Microsoft.Extensions.Hosting;

namespace Orleans
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    internal class ClusterClient : IInternalClusterClient, IHostedService
    {
        private readonly OutsideRuntimeClient _runtimeClient;
        private readonly ILogger<ClusterClient> _logger;
        private readonly ClusterClientLifecycle _clusterClientLifecycle;

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClient"/> class.
        /// </summary>
        /// <param name="serviceProvider">The service provider.</param>
        /// <param name="runtimeClient">The runtime client.</param>
        /// <param name="loggerFactory">Logger factory used to create loggers</param>
        /// <param name="clientMessagingOptions">Messaging parameters</param>
        public ClusterClient(IServiceProvider serviceProvider, OutsideRuntimeClient runtimeClient, ILoggerFactory loggerFactory, IOptions<ClientMessagingOptions> clientMessagingOptions)
        {
            ValidateSystemConfiguration(serviceProvider);

            _runtimeClient = runtimeClient;
            _logger = loggerFactory.CreateLogger<ClusterClient>();
            _clusterClientLifecycle = new ClusterClientLifecycle(_logger);

            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<IClusterClientLifecycle>> lifecycleParticipants = ServiceProvider.GetServices<ILifecycleParticipant<IClusterClientLifecycle>>();
            foreach (var participant in lifecycleParticipants)
            {
                participant?.Participate(_clusterClientLifecycle);
            }

            // register all named lifecycle participants
            var namedLifecycleParticipantCollection = ServiceProvider.GetService<IKeyedServiceCollection<string, ILifecycleParticipant<IClusterClientLifecycle>>>();
            if (namedLifecycleParticipantCollection?.GetServices(ServiceProvider)?.Select(s => s.GetService(ServiceProvider)) is { } namedParticipants)
            {
                foreach (var participant in namedParticipants)
                {
                    participant.Participate(_clusterClientLifecycle);
                }
            }

            static void ValidateSystemConfiguration(IServiceProvider serviceProvider)
            {
                var validators = serviceProvider.GetServices<IConfigurationValidator>();
                foreach (var validator in validators)
                {
                    validator.ValidateConfiguration();
                }
            }
        }

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => _runtimeClient.ServiceProvider;

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _runtimeClient.Start(cancellationToken).ConfigureAwait(false);
            await _clusterClientLifecycle.OnStart(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("Client shutting down");

                await _clusterClientLifecycle.OnStop(cancellationToken).ConfigureAwait(false);

                _runtimeClient?.Reset();
            }
            finally
            {
                _logger.LogInformation("Client shutdown completed");
            }
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver => ((IGrainFactory)_runtimeClient.InternalGrainFactory).CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver => _runtimeClient.InternalGrainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable => _runtimeClient.InternalGrainFactory.CreateObjectReference<TGrainObserverInterface>(obj);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination) => _runtimeClient.InternalGrainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId) where TGrainInterface : ISystemTarget => _runtimeClient.InternalGrainFactory.GetSystemTarget<TGrainInterface>(grainId);

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain) => _runtimeClient.InternalGrainFactory.Cast<TGrainInterface>(grain);

        /// <inheritdoc />
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId) => _runtimeClient.InternalGrainFactory.GetGrain<TGrainInterface>(grainId);

        /// <inheritdoc />
        IAddressable IGrainFactory.GetGrain(GrainId grainId) => _runtimeClient.InternalGrainFactory.GetGrain(grainId);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        /// <inheritdoc />
        public object Cast(IAddressable grain, Type outputGrainInterfaceType)
            => _runtimeClient.InternalGrainFactory.Cast(grain, outputGrainInterfaceType);

        /// <inheritdoc />
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => _runtimeClient.InternalGrainFactory.GetGrain(grainId, interfaceType);
    }
}