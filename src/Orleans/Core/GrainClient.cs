using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans
{
    /// <summary>
    /// Handler for client disconnection from a cluster.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    public delegate void ConnectionToClusterLostHandler(object sender, EventArgs e);

    /// <summary>
    /// The delegate called before every request to a grain.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="grain">The grain.</param>
    public delegate void ClientInvokeCallback(InvokeMethodRequest request, IGrain grain);

    /// <summary>
    /// Client runtime for connecting to Orleans system
    /// </summary>
    /// TODO: Make this class non-static and inject it where it is needed.
    public static class GrainClient
    {
        /// <summary>
        /// Whether the client runtime has already been initialized
        /// </summary>
        /// <returns><c>true</c> if client runtime is already initialized</returns>
        public static bool IsInitialized => isFullyInitialized && client.IsInitialized;

        internal static ClientConfiguration CurrentConfig => client.Configuration;
        internal static bool TestOnlyNoConnect { get; set; }

        private static bool isFullyInitialized = false;

        private static IInternalClusterClient client;

        private static readonly object initLock = new Object();
        
        public static IClusterClient Instance => client;

        //TODO: prevent client code from using this from inside a Grain or provider
        public static IGrainFactory GrainFactory => GetGrainFactory();

        private static IGrainFactory GetGrainFactory()
        {
            if (!IsInitialized)
            {
                throw new OrleansException("You must initialize the Grain Client before accessing the GrainFactory");
            }

            return client;
        }

        /// <summary>
        /// Initializes the client runtime from the standard client configuration file.
        /// </summary>
        public static void Initialize()
        {
            ClientConfiguration config = ClientConfiguration.StandardLoad();
            if (config == null)
            {
                Console.WriteLine("Error loading standard client configuration file.");
                throw new ArgumentException("Error loading standard client configuration file");
            }
            var orleansClient = (IInternalClusterClient)new ClientBuilder().UseConfiguration(config).Build();
            InternalInitialize(orleansClient);
        }

        /// <summary>
        /// Initializes the client runtime from the provided client configuration file.
        /// If an error occurs reading the specified configuration file, the initialization fails.
        /// </summary>
        /// <param name="configFilePath">A relative or absolute pathname for the client configuration file.</param>
        public static void Initialize(string configFilePath)
        {
            Initialize(new FileInfo(configFilePath));
        }

        /// <summary>
        /// Initializes the client runtime from the provided client configuration file.
        /// If an error occurs reading the specified configuration file, the initialization fails.
        /// </summary>
        /// <param name="configFile">The client configuration file.</param>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters")]
        public static void Initialize(FileInfo configFile)
        {
            ClientConfiguration config;
            try
            {
                config = ClientConfiguration.LoadFromFile(configFile.FullName);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error loading client configuration file {0}: {1}", configFile.FullName, ex);
                throw;
            }
            if (config == null)
            {
                Console.WriteLine("Error loading client configuration file {0}:", configFile.FullName);
                throw new ArgumentException(string.Format("Error loading client configuration file {0}:", configFile.FullName), nameof(configFile));
            }
            var orleansClient = (IInternalClusterClient)new ClientBuilder().UseConfiguration(config).Build();
            InternalInitialize(orleansClient);
        }

        /// <summary>
        /// Initializes the client runtime from the provided client configuration object.
        /// If the configuration object is null, the initialization fails.
        /// </summary>
        /// <param name="config">A ClientConfiguration object.</param>
        public static void Initialize(ClientConfiguration config)
        {
            if (config == null)
            {
                Console.WriteLine("Initialize was called with null ClientConfiguration object.");
                throw new ArgumentException("Initialize was called with null ClientConfiguration object.", nameof(config));
            }
            var orleansClient = (IInternalClusterClient)new ClientBuilder().UseConfiguration(config).Build();
            InternalInitialize(orleansClient);
        }

        /// <summary>
        /// Initializes the client runtime from the standard client configuration file using the provided gateway address.
        /// Any gateway addresses specified in the config file will be ignored and the provided gateway address wil be used instead.
        /// </summary>
        /// <param name="gatewayAddress">IP address and port of the gateway silo</param>
        /// <param name="overrideConfig">Whether the specified gateway endpoint should override / replace the values from config file, or be additive</param>
        [Obsolete(
             "This method is obsolete and will be removed in a future release. If it is needed, reimplement this method "
             + "according to its source: "
             + "https://github.com/dotnet/orleans/blob/8f067e77f115c0599eff00c2bdc6f37f2adbc58d/src/Orleans/Core/GrainClient.cs#L173"
         )]
        public static void Initialize(IPEndPoint gatewayAddress, bool overrideConfig = true)
        {
            var config = ClientConfiguration.StandardLoad();
            if (config == null)
            {
                Console.WriteLine("Error loading standard client configuration file.");
                throw new ArgumentException("Error loading standard client configuration file");
            }
            if (overrideConfig)
            {
                config.Gateways = new List<IPEndPoint>(new[] { gatewayAddress });
            }
            else if (!config.Gateways.Contains(gatewayAddress))
            {
                config.Gateways.Add(gatewayAddress);
            }
            config.PreferedGatewayIndex = config.Gateways.IndexOf(gatewayAddress);
            var orleansClient = (IInternalClusterClient)new ClientBuilder().UseConfiguration(config).Build();
            InternalInitialize(orleansClient);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static void InternalInitialize(IInternalClusterClient clusterClient)
        {
            if (TestOnlyNoConnect)
            {
                Trace.TraceInformation("TestOnlyNoConnect - Returning before connecting to cluster.");
            }
            else
            {
                // Finish initializing this client connection to the Orleans cluster
                DoInternalInitialize(clusterClient);
            }
        }

        /// <summary>
        /// Initializes client runtime from client configuration object.
        /// </summary>
        private static void DoInternalInitialize(IInternalClusterClient clusterClient)
        {
            if (IsInitialized)
                return;

            lock (initLock)
            {
                if (!IsInitialized)
                {
                    try
                    {
                        // this is probably overkill, but this ensures isFullyInitialized false
                        // before we make a call that makes RuntimeClient.Current not null
                        isFullyInitialized = false;
                        client = clusterClient;  // Keep reference, to avoid GC problems
                        client.Connect().GetAwaiter().GetResult();

                        // this needs to be the last successful step inside the lock so
                        // IsInitialized doesn't return true until we're fully initialized
                        isFullyInitialized = true;
                    }
                    catch (Exception exc)
                    {
                        // just make sure to fully Uninitialize what we managed to partially initialize, so we don't end up in inconsistent state and can later on re-initialize.
                        Console.WriteLine("Initialization failed. {0}", exc);
                        InternalUninitialize();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Uninitializes client runtime.
        /// </summary>
        public static void Uninitialize()
        {
            lock (initLock)
            {
                InternalUninitialize();
            }
        }

        /// <summary>
        /// Test hook to uninitialize client without cleanup
        /// </summary>
        public static void HardKill()
        {
            lock (initLock)
            {
                InternalUninitialize(false);
            }
        }

        /// <summary>
        /// This is the lock free version of uninitilize so we can share
        /// it between the public method and error paths inside initialize.
        /// This should only be called inside a lock(initLock) block.
        /// </summary>
        private static void InternalUninitialize(bool cleanup = true)
        {
            // Update this first so IsInitialized immediately begins returning
            // false.  Since this method should be protected externally by
            // a lock(initLock) we should be able to reset everything else
            // before the next init attempt.
            isFullyInitialized = false;
            
            try
            {
                if (cleanup)
                {
                    client?.Close().GetAwaiter().GetResult();
                }
                else
                {
                    client?.Abort();
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                client?.Dispose();
                client = null;
            }
        }

        /// <summary>
        /// Check that the runtime is intialized correctly, and throw InvalidOperationException if not
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        private static void CheckInitialized()
        {
            if (!IsInitialized)
                throw new InvalidOperationException("Runtime is not initialized. Call Client.Initialize method to initialize the runtime.");
        }

        /// <summary>
        /// Provides logging facility for applications.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        public static Logger Logger
        {
            get
            {
                CheckInitialized();
                return client.Logger;
            }
        }

        /// <summary>
        /// Set a timeout for responses on this Orleans client.
        /// </summary>
        /// <param name="timeout"></param>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        public static void SetResponseTimeout(TimeSpan timeout)
        {
            CheckInitialized();
            RuntimeClient.SetResponseTimeout(timeout);
        }

        /// <summary>
        /// Get a timeout of responses on this Orleans client.
        /// </summary>
        /// <returns>The response timeout.</returns>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        public static TimeSpan GetResponseTimeout()
        {
            CheckInitialized();
            return RuntimeClient.GetResponseTimeout();
        }

        /// <summary>
        /// Global pre-call interceptor function
        /// Synchronous callback made just before a message is about to be constructed and sent by a client to a grain.
        /// This call will be made from the same thread that constructs the message to be sent, so any thread-local settings
        /// such as <c>Orleans.RequestContext</c> will be picked up.
        /// The action receives an <see cref="InvokeMethodRequest"/> with details of the method to be invoked, including InterfaceId and MethodId,
        /// and a <see cref="IGrain"/> which is the GrainReference this request is being sent through
        /// </summary>
        /// <remarks>This callback method should return promptly and do a minimum of work, to avoid blocking calling thread or impacting throughput.</remarks>
        public static ClientInvokeCallback ClientInvokeCallback
        {
            get
            {
                CheckInitialized();
                return RuntimeClient.ClientInvokeCallback;
            }
            set
            {
                CheckInitialized();
                RuntimeClient.ClientInvokeCallback = value;
            }
        }

        public static IEnumerable<Streams.IStreamProvider> GetStreamProviders()
        {
            CheckInitialized();
            return client.GetStreamProviders();
        }

        internal static IStreamProviderRuntime CurrentStreamProviderRuntime
        {
            get
            {
                CheckInitialized();
                return client.StreamProviderRuntime;
            }
        }

        public static IStreamProvider GetStreamProvider(string name)
        {
            CheckInitialized();
            return client.GetStreamProvider(name);
        }

        public static event ConnectionToClusterLostHandler ClusterConnectionLost
        {
            add
            {
                CheckInitialized();
                RuntimeClient.ClusterConnectionLost += value;
            }

            remove
            {
                CheckInitialized();
                RuntimeClient.ClusterConnectionLost -= value;
            }
        }

        private static OutsideRuntimeClient RuntimeClient => client.ServiceProvider.GetRequiredService<OutsideRuntimeClient>();
    }
}