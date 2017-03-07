using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Client for communicating with clusters of Orleans silos.
    /// </summary>
    public class ClusterClient : IInternalClusterClient
    {
        private readonly OutsideRuntimeClient runtimeClient;
        private readonly AsyncLock initLock = new AsyncLock();
        private LifecycleState state = LifecycleState.Created;

        private enum LifecycleState
        {
            Created,
            Started,
            Disposed
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ClusterClient"/> class.
        /// </summary>
        /// <param name="configuration">The client configuration.</param>
        public ClusterClient(ClientConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentException(nameof(configuration));
            }

            this.Configuration = configuration;
            this.runtimeClient = new OutsideRuntimeClient(configuration);
        }

        /// <inheritdoc />
        public bool IsInitialized => this.state == LifecycleState.Started;

        /// <inheritdoc />
        public IGrainFactory GrainFactory => this.InternalGrainFactory;

        /// <inheritdoc />
        internal IInternalGrainFactory InternalGrainFactory
        {
            get
            {
                this.ThrowIfDisposedOrNotInitialized();
                return this.runtimeClient.InternalGrainFactory;
            }
        }

        /// <inheritdoc />
        public Logger Logger
        {
            get
            {
                this.ThrowIfDisposedOrNotInitialized();
                return this.runtimeClient.AppLogger;
            }
        }

        /// <inheritdoc />
        public IServiceProvider ServiceProvider => this.runtimeClient.ServiceProvider;

        /// <inheritdoc />
        public TimeSpan ResponseTimeout
        {
            get { return this.runtimeClient.GetResponseTimeout(); }

            set { this.runtimeClient.SetResponseTimeout(value); }
        }

        /// <inheritdoc />
        public Action<InvokeMethodRequest, IGrain> ClientInvokeCallback
        {
            get { return this.runtimeClient.ClientInvokeCallback; }
            set { this.runtimeClient.ClientInvokeCallback = value; }
        }

        /// <inheritdoc />
        public ClientConfiguration Configuration { get; }

        /// <inheritdoc />
        IStreamProviderRuntime IInternalClusterClient.StreamProviderRuntime => this.runtimeClient.CurrentStreamProviderRuntime;

        /// <inheritdoc />
        public IEnumerable<IStreamProvider> GetStreamProviders()
        {
            this.ThrowIfDisposedOrNotInitialized();
            return this.runtimeClient.CurrentStreamProviderManager.GetStreamProviders();
        }

        /// <inheritdoc />
        public IStreamProvider GetStreamProvider(string name)
        {
            this.ThrowIfDisposedOrNotInitialized();
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            return this.runtimeClient.CurrentStreamProviderManager.GetProvider(name) as IStreamProvider;
        }

        /// <inheritdoc />
        public event ConnectionToClusterLostHandler ClusterConnectionLost
        {
            add
            {
                this.ThrowIfDisposed();
                this.runtimeClient.ClusterConnectionLost += value;
            }

            remove
            {
                this.ThrowIfDisposed();
                this.runtimeClient.ClusterConnectionLost -= value;
            }
        }

        /// <summary>
        /// Creates a new <see cref="ClusterClient"/>, loading configuration using the default method.
        /// </summary>
        /// <returns></returns>
        public static ClusterClient Create() => new ClusterClient(ClientConfiguration.StandardLoad());

        /// <summary>
        /// Creates a new <see cref="ClusterClient"/>, loading configuration from the specified file.
        /// </summary>
        /// <param name="configFilePath">The configuration file.</param>
        /// <returns>A newly instantiated <see cref="ClusterClient"/>.</returns>
        public static ClusterClient Create(string configFilePath) => Create(new FileInfo(configFilePath));

        /// <summary>
        /// Creates a new <see cref="ClusterClient"/>, loading configuration from the specified file.
        /// </summary>
        /// <param name="configFile">The configuration file.</param>
        /// <returns>A newly instantiated <see cref="ClusterClient"/>.</returns>
        public static ClusterClient Create(FileInfo configFile)
        {
            var config = ClientConfiguration.LoadFromFile(configFile.FullName);
            if (config == null)
            {
                throw new ArgumentException(
                    $"Error loading client configuration file {configFile.FullName}",
                    nameof(configFile));
            }

            return new ClusterClient(config);
        }

        /// <summary>
        /// Creates a new <see cref="ClusterClient"/> using the provided configuration.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <returns>A newly instantiated <see cref="ClusterClient"/>.</returns>
        public static ClusterClient Create(ClientConfiguration configuration) => new ClusterClient(configuration);

        /// <inheritdoc />
        public async Task Start()
        {
            this.ThrowIfDisposed();
            using (await this.initLock.LockAsync())
            {
                await this.runtimeClient.Start();
                this.state = LifecycleState.Started;
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            this.Stop(gracefully: true).Wait();
        }

        /// <inheritdoc />
        public void Abort()
        {
            this.Stop(gracefully: false).Wait();
        }

        private async Task Stop(bool gracefully)
        {
            if (this.state == LifecycleState.Disposed) return;

            using (await this.initLock.LockAsync())
            {
                if (this.state == LifecycleState.Disposed) return;
                Utils.SafeExecute(() => this.runtimeClient.Reset(gracefully));
                this.Dispose();
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            this.Dispose(true);
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
            if (!this.IsInitialized) throw new InvalidOperationException("Client is not initialized.");
        }

        private void ThrowIfDisposed()
        {
            if (this.state == LifecycleState.Disposed)
                throw new ObjectDisposedException(
                    nameof(ClusterClient),
                    $"Client has been disposed either by a call to {nameof(Dispose)} or because it has been stopped.");
        }
    }
}