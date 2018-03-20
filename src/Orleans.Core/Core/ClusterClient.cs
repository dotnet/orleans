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
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    internal class ClusterClient : IInternalClusterClient
    {
        private readonly OutsideRuntimeClient runtimeClient;
        private readonly ClusterClientLifecycle clusterClientLifecycle;
        private readonly AsyncLock initLock = new AsyncLock();
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
            this.clusterClientLifecycle = new ClusterClientLifecycle(loggerFactory.CreateLogger<LifecycleSubject>());

            //set PropagateActivityId flag from node cofnig
            RequestContext.PropagateActivityId = clientMessagingOptions.Value.PropagateActivityId;

            // register all lifecycle participants
            IEnumerable<ILifecycleParticipant<IClusterClientLifecycle>> lifecycleParticipants = this.ServiceProvider.GetServices<ILifecycleParticipant<IClusterClientLifecycle>>();
            foreach (ILifecycleParticipant<IClusterClientLifecycle> participant in lifecycleParticipants)
            {
                participant?.Participate(clusterClientLifecycle);
            }

            // register all named lifecycle participants
            IKeyedServiceCollection<string, ILifecycleParticipant<IClusterClientLifecycle>> namedLifecycleParticipantCollections = this.ServiceProvider.GetService<IKeyedServiceCollection<string, ILifecycleParticipant<IClusterClientLifecycle>>>();
            foreach (ILifecycleParticipant<IClusterClientLifecycle> participant in namedLifecycleParticipantCollections
                ?.GetServices(this.ServiceProvider)
                ?.Select(s => s?.GetService(this.ServiceProvider)))
            {
                participant?.Participate(clusterClientLifecycle);
            }
        }

        /// <inheritdoc />
        public bool IsInitialized => this.state == LifecycleState.Started;

        /// <inheritdoc />
        public IGrainFactory GrainFactory => this.InternalGrainFactory;

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        /// <inheritdoc />
        IStreamProviderRuntime IInternalClusterClient.StreamProviderRuntime => this.runtimeClient.CurrentStreamProviderRuntime;

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
        private bool IsDisposing => this.state == LifecycleState.Disposed ||
                                    this.state == LifecycleState.Disposing;

        /// <inheritdoc />
        public IStreamProvider GetStreamProvider(string name)
        {
            this.ThrowIfDisposedOrNotInitialized();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return this.runtimeClient.ServiceProvider.GetRequiredServiceByName<IStreamProvider>(name);
        }

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
        }

        /// <inheritdoc />
        public Task Close() => this.Stop(gracefully: true);

        /// <inheritdoc />
        public void Abort()
        {
            this.Stop(gracefully: false).GetAwaiter().GetResult();
        }

        private async Task Stop(bool gracefully)
        {
            if (this.IsDisposing) return;
            using (await this.initLock.LockAsync().ConfigureAwait(false))
            {
                if (this.state == LifecycleState.Disposed) return;
                try
                {
                    this.state = LifecycleState.Disposing;
                    CancellationToken canceled = CancellationToken.None;
                    if (!gracefully)
                    {
                        var cts = new CancellationTokenSource();
                        cts.Cancel();
                        canceled = cts.Token;
                    }
                    await this.clusterClientLifecycle.OnStop(canceled);
                    if (gracefully)
                    {
                        Utils.SafeExecute(() => this.runtimeClient.Disconnect());
                    }

                    Utils.SafeExecute(() => this.runtimeClient.Reset(gracefully));
                    this.Dispose(true);
                }
                finally
                {
                    // If disposal failed, the system is in an invalid state.
                    if (this.state == LifecycleState.Disposing) this.state = LifecycleState.Invalid;
                }
            }
        }

        /// <inheritdoc />
        void IDisposable.Dispose() => this.Abort();

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
        public void BindGrainReference(IAddressable grain)
        {
            this.InternalGrainFactory.BindGrainReference(grain);
        }

        /// <inheritdoc />
        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IAddressable obj)
            where TGrainObserverInterface : IAddressable
        {
            return this.InternalGrainFactory.CreateObjectReference<TGrainObserverInterface>(obj);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetSystemTarget<TGrainInterface>(GrainId grainId, SiloAddress destination)
        {
            return this.InternalGrainFactory.GetSystemTarget<TGrainInterface>(grainId, destination);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.Cast<TGrainInterface>(IAddressable grain)
        {
            return this.InternalGrainFactory.Cast<TGrainInterface>(grain);
        }

        /// <inheritdoc />
        object IInternalGrainFactory.Cast(IAddressable grain, Type interfaceType)
        {
            return this.InternalGrainFactory.Cast(grain, interfaceType);
        }

        /// <inheritdoc />
        TGrainInterface IInternalGrainFactory.GetGrain<TGrainInterface>(GrainId grainId)
        {
            return this.InternalGrainFactory.GetGrain<TGrainInterface>(grainId);
        }

        /// <inheritdoc />
        GrainReference IInternalGrainFactory.GetGrain(GrainId grainId, string genericArguments)
        {
            return this.InternalGrainFactory.GetGrain(grainId, genericArguments);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Usage", "CA2213:DisposableFieldsShouldBeDisposed")]
        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                Utils.SafeExecute(() => this.runtimeClient.Dispose());
                this.state = LifecycleState.Disposed;
            }

            GC.SuppressFinalize(this);
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
                throw new ObjectDisposedException(
                    nameof(ClusterClient),
                    $"Client has been disposed either by a call to {nameof(Dispose)} or because it has been stopped.");
        }
    }
}