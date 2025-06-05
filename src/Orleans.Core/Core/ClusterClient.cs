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
    internal partial class ClusterClient : IInternalClusterClient, IHostedService
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
            runtimeClient.ConsumeServices();

            _runtimeClient = runtimeClient;
            _logger = loggerFactory.CreateLogger<ClusterClient>();
            _clusterClientLifecycle = new ClusterClientLifecycle(_logger);

            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<IClusterClientLifecycle>> lifecycleParticipants = ServiceProvider.GetServices<ILifecycleParticipant<IClusterClientLifecycle>>();
            foreach (var participant in lifecycleParticipants)
            {
                participant?.Participate(_clusterClientLifecycle);
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
            await _runtimeClient.StartAsync(cancellationToken).ConfigureAwait(false);
            await _clusterClientLifecycle.OnStart(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                LogClientShuttingDown(_logger);

                await _clusterClientLifecycle.OnStop(cancellationToken).ConfigureAwait(false);
                await _runtimeClient.StopAsync(cancellationToken).WaitAsync(cancellationToken);
            }
            finally
            {
                LogClientShutdownCompleted(_logger);
            }
        }

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
        public object Cast(IAddressable grain, Type outputGrainInterfaceType)
            => _runtimeClient.InternalGrainFactory.Cast(grain, outputGrainInterfaceType);

        /// <inheritdoc />
        public GrainInterfaceType GetGrainInterfaceType(Type interfaceType) => _runtimeClient.InternalGrainFactory.GetGrainInterfaceType(interfaceType);
     
        /// <inheritdoc />
        public IAddressable CreateGrainReference(GrainId grainId, GrainInterfaceType interfaceType) => _runtimeClient.InternalGrainFactory.CreateGrainReference(grainId, interfaceType);
   
        /// <inheritdoc />
        public GrainType GetGrainType(GrainInterfaceType grainInterfaceType, string grainClassNamePrefix = null) => _runtimeClient.InternalGrainFactory.GetGrainType(grainInterfaceType, grainClassNamePrefix);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Client shutting down."
        )]
        private static partial void LogClientShuttingDown(ILogger logger);

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Client shutdown completed."
        )]
        private static partial void LogClientShutdownCompleted(ILogger logger);
    }
}