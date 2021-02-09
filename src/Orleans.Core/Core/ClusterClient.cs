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
using Orleans.Hosting;

namespace Orleans
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    internal class ClusterClient : IInternalClusterClient
    {
        private readonly OutsideRuntimeClient runtimeClient;
        private readonly ILogger<ClusterClient> logger;
        private readonly ClusterClientLifecycle clusterClientLifecycle;
        private readonly AsyncLock initLock = new AsyncLock();
        private readonly ClientApplicationLifetime applicationLifetime;
        private LifecycleState state = LifecycleState.Created;

        private enum LifecycleState
        {
            Invalid,
            Created,
            Starting,
            Started,
            Disposing,
            Disposed,
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClient"/> class.
        /// </summary>
        /// <param name="runtimeClient">The runtime client.</param>
        /// <param name="loggerFactory">Logger factory used to create loggers</param>
        /// <param name="clientMessagingOptions">Messaging parameters</param>
        public ClusterClient(OutsideRuntimeClient runtimeClient, ILoggerFactory loggerFactory, IOptions<ClientMessagingOptions> clientMessagingOptions)
        {
            this.runtimeClient = runtimeClient;
            this.logger = loggerFactory.CreateLogger<ClusterClient>();
            this.clusterClientLifecycle = new ClusterClientLifecycle(loggerFactory.CreateLogger<LifecycleSubject>());

            //set PropagateActivityId flag from node config
            RequestContext.PropagateActivityId |= clientMessagingOptions.Value.PropagateActivityId;

            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<IClusterClientLifecycle>> lifecycleParticipants = this.ServiceProvider.GetServices<ILifecycleParticipant<IClusterClientLifecycle>>();
            foreach (var participant in lifecycleParticipants)
            {
                participant?.Participate(clusterClientLifecycle);
            }

            // register all named lifecycle participants
            IKeyedServiceCollection<string, ILifecycleParticipant<IClusterClientLifecycle>> namedLifecycleParticipantCollections = this.ServiceProvider.GetService<IKeyedServiceCollection<string, ILifecycleParticipant<IClusterClientLifecycle>>>();
            foreach (var participant in namedLifecycleParticipantCollections
                ?.GetServices(this.ServiceProvider)
                ?.Select(s => s?.GetService(this.ServiceProvider)))
            {
                participant?.Participate(clusterClientLifecycle);
            }

            // It is fine for this field to be null in the case that the client is not the host.
            this.applicationLifetime = runtimeClient.ServiceProvider.GetService<IHostApplicationLifetime>() as ClientApplicationLifetime;
        }

        /// <inheritdoc />
        public bool IsInitialized => this.state == LifecycleState.Started;

        /// <inheritdoc />
        public IGrainFactory GrainFactory => this.InternalGrainFactory;

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        /// <inheritdoc />
        private IInternalGrainFactory InternalGrainFactory
        {
            get
            {
                this.ThrowIfDisposedOrNotInitialized();
                return this.runtimeClient.InternalGrainFactory;
            }
        }

        /// <summary>
        /// Gets a value indicating whether or not this instance is being disposed.
        /// </summary>
        private bool IsDisposing => this.state switch
        {
            LifecycleState.Disposing => true,
            LifecycleState.Disposed => true,
            LifecycleState.Invalid => true,
            _ => false
        };

        /// <inheritdoc />
        public async Task Connect(Func<Exception, Task<bool>> retryFilter = null)
        {
            this.ThrowIfDisposedOrAlreadyInitialized();
            using (await this.initLock.LockAsync().ConfigureAwait(false))
            {
                this.ThrowIfDisposedOrAlreadyInitialized();
                if (this.state == LifecycleState.Starting)
                {
                    throw new InvalidOperationException("A prior connection attempt failed. This instance must be disposed.");
                }

                this.state = LifecycleState.Starting;
                await this.runtimeClient.Start(retryFilter).ConfigureAwait(false);
                await this.clusterClientLifecycle.OnStart().ConfigureAwait(false);
                this.state = LifecycleState.Started;
            }

            this.applicationLifetime?.NotifyStarted();
        }

        /// <inheritdoc />
        public Task Close() => this.StopAsync(gracefully: true);

        /// <inheritdoc />
        public Task AbortAsync() => this.StopAsync(gracefully: false);

        private async Task StopAsync(bool gracefully)
        {
            if (this.IsDisposing) return;

            this.applicationLifetime?.StopApplication();
            using (await this.initLock.LockAsync().ConfigureAwait(false))
            {
                if (this.state == LifecycleState.Disposed) return;
                try
                {
                    logger.LogInformation("Client shutting down");

                    this.state = LifecycleState.Disposing;
                    CancellationToken canceled = CancellationToken.None;
                    if (!gracefully)
                    {
                        var cts = new CancellationTokenSource();
                        cts.Cancel();
                        canceled = cts.Token;
                    }

                    await this.clusterClientLifecycle.OnStop(canceled).ConfigureAwait(false);

                    this.runtimeClient?.Reset(gracefully);
                    this.state = LifecycleState.Disposed;
                }
                finally
                {
                    logger.LogInformation("Client shutdown completed");

                    // If disposal failed, the system is in an invalid state.
                    if (this.state == LifecycleState.Disposing) this.state = LifecycleState.Invalid;
                }
            }

            this.applicationLifetime?.NotifyStopped();
        }

        /// <inheritdoc />
        void IDisposable.Dispose()
        {
            if (this.IsDisposing) return;

            this.AbortAsync().GetAwaiter().GetResult();

            // Only dispose the service container if this client owns the application lifetime.
            // If the lifetime isn't owned by this client, then the owner is responsible for disposing the container.
            if (this.applicationLifetime is object)
            {
                (this.ServiceProvider as IDisposable)?.Dispose();
            }

            this.state = LifecycleState.Disposed;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (this.IsDisposing) return;

            await this.AbortAsync().ConfigureAwait(false);

            // Only dispose the service container if this client owns the application lifetime.
            // If the lifetime isn't owned by this client, then the owner is responsible for disposing the container.
            if (this.applicationLifetime is object)
            {
                switch (this.ServiceProvider)
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposabe:
                        await Task.Run(() => disposabe.Dispose()).ConfigureAwait(false);
                        break;
                }
            }

            this.state = LifecycleState.Disposed;
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(primaryKey, keyExtension, grainClassNamePrefix);
        }

        /// <inheritdoc />
        public Task<TGrainObserverInterface> CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
        {
            return ((IGrainFactory) this.runtimeClient.InternalGrainFactory).CreateObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        public Task DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj) where TGrainObserverInterface : IGrainObserver
        {
            return this.InternalGrainFactory.DeleteObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable
        {
            return this.InternalGrainFactory.CreateObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainType grainType, SiloAddress destination)
        {
            return this.InternalGrainFactory.GetSystemTarget<TGrainInterface>(grainType, destination);
        }

        public TGrainInterface GetSystemTarget<TGrainInterface>(GrainId grainId) where TGrainInterface : ISystemTarget
        {
            return this.InternalGrainFactory.GetSystemTarget<TGrainInterface>(grainId);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain)
        {
            return this.InternalGrainFactory.Cast<TGrainInterface>(grain);
        }

        /// <inheritdoc />
        TGrainInterface IGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(grainId);
        }

        /// <inheritdoc />
        IAddressable IGrainFactory.GetGrain(GrainId grainId)
        {
            return this.InternalGrainFactory.GetGrain(grainId);
        }

        private void ThrowIfDisposedOrNotInitialized()
        {
            this.ThrowIfDisposed();
            if (!this.IsInitialized) throw new InvalidOperationException($"Client is not initialized. Current client state is {this.state}.");
        }

        private void ThrowIfDisposedOrAlreadyInitialized()
        {
            this.ThrowIfDisposed();
            if (this.IsInitialized) throw new InvalidOperationException("Client is already initialized.");
        }

        private void ThrowIfDisposed()
        {
            if (this.IsDisposing)
            {
                throw new ObjectDisposedException(nameof(ClusterClient), "Client has been disposed.");
            }
        }

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)
            => this.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)
            => this.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey)
            => this.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => this.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        /// <inheritdoc />
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => this.InternalGrainFactory.GetGrain(grainInterfaceType, grainPrimaryKey, keyExtension);

        /// <inheritdoc />
        public object Cast(IAddressable grain, Type outputGrainInterfaceType)
            => this.InternalGrainFactory.Cast(grain, outputGrainInterfaceType);

        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType)
            => this.InternalGrainFactory.GetGrain(grainId, interfaceType);
    }
}