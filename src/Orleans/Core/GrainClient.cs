using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.Streams;

namespace Orleans
{
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
        public static bool IsInitialized { get { return isFullyInitialized && RuntimeClient.Current != null; } }

        internal static ClientConfiguration CurrentConfig { get; private set; }
        internal static bool TestOnlyNoConnect { get; set; }

        private static bool isFullyInitialized = false;

        private static OutsideRuntimeClient outsideRuntimeClient;

        private static readonly object initLock = new Object();

        private static GrainFactory grainFactory;

        // RuntimeClient.Current is set to something different than OutsideRuntimeClient - it can only be set to InsideRuntimeClient, since we only have 2.
        // That means we are running in side a silo.
        private static bool IsRunningInsideSilo { get { return RuntimeClient.Current != null && !(RuntimeClient.Current is OutsideRuntimeClient); } }

        //TODO: prevent client code from using this from inside a Grain or provider
        public static IGrainFactory GrainFactory
        {
            get
            {
                if (IsRunningInsideSilo)
                {
                    // just in case, make sure we don't get NullRefExc when checking RuntimeContext.
                    bool runningInsideGrain = RuntimeContext.Current != null && RuntimeContext.CurrentActivationContext != null
                        && RuntimeContext.CurrentActivationContext.ContextType == SchedulingContextType.Activation;
                    if (runningInsideGrain)
                    {
                        throw new OrleansException("You are running inside a grain. GrainClient.GrainFactory should only be used on the client side. " +
                                 "Inside a grain use GrainFactory property of the Grain base class (use this.GrainFactory).");

                    }
                    else // running inside provider or else where
                    {
                        throw new OrleansException("You are running inside the provider code, on the silo. GrainClient.GrainFactory should only be used on the client side. " +
                                "Inside the provider code use GrainFactory that is passed via IProviderRuntime (use providerRuntime.GrainFactory).");
                    }
                }

                if (!IsInitialized)
                {
               
                    throw new OrleansException("You must initialize the Grain Client before accessing the GrainFactory");
                }

                return grainFactory;
            }
        }

        internal static GrainFactory InternalGrainFactory
        {
            get
            {
                if (!IsInitialized)
                {
                    throw new OrleansException("You must initialize the Grain Client before accessing the InternalGrainFactory");
                }

                return grainFactory;
            }
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
            InternalInitialize(config);
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
                throw new ArgumentException(String.Format("Error loading client configuration file {0}:", configFile.FullName), "configFile");
            }
            InternalInitialize(config);
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
                throw new ArgumentException("Initialize was called with null ClientConfiguration object.", "config");
            }
            InternalInitialize(config);
        }

        /// <summary>
        /// Initializes the client runtime from the standard client configuration file using the provided gateway address.
        /// Any gateway addresses specified in the config file will be ignored and the provided gateway address wil be used instead. 
        /// </summary>
        /// <param name="gatewayAddress">IP address and port of the gateway silo</param>
        /// <param name="overrideConfig">Whether the specified gateway endpoint should override / replace the values from config file, or be additive</param>
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
            InternalInitialize(config);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private static void InternalInitialize(ClientConfiguration config, OutsideRuntimeClient runtimeClient = null)
        {
            // We deliberately want to run this initialization code on .NET thread pool thread to escape any 
            // TPL execution environment and avoid any conflicts with client's synchronization context
            var tcs = new TaskCompletionSource<ClientConfiguration>();
            WaitCallback doInit = state =>
            {
                try
                {
                    if (TestOnlyNoConnect)
                    {
                        Trace.TraceInformation("TestOnlyNoConnect - Returning before connecting to cluster.");
                    }
                    else
                    {
                        // Finish initializing this client connection to the Orleans cluster
                        DoInternalInitialize(config, runtimeClient);
                    }
                    tcs.SetResult(config); // Resolve promise
                }
                catch (Exception exc)
                {
                    tcs.SetException(exc); // Break promise
                }
            };

            // Queue Init call to thread pool thread
            ThreadPool.QueueUserWorkItem(doInit, null);
            try
            {
                CurrentConfig = tcs.Task.Result; // Wait for Init to finish
            }
            catch (AggregateException ae)
            {
                // Flatten the aggregate exception, which can be deeply nested.
                ae = ae.Flatten();

                // If there is just one exception in the aggregate exception, throw that, otherwise throw the entire
                // flattened aggregate exception.
                var innerExceptions = ae.InnerExceptions;
                var exceptionToThrow = innerExceptions.Count == 1 ? innerExceptions[0] : ae;
                ExceptionDispatchInfo.Capture(exceptionToThrow).Throw();
            }
        }

        /// <summary>
        /// Initializes client runtime from client configuration object.
        /// </summary>
        private static void DoInternalInitialize(ClientConfiguration config, OutsideRuntimeClient runtimeClient = null)
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
                        grainFactory = new GrainFactory();

                        if (runtimeClient == null)
                        {
                            runtimeClient = new OutsideRuntimeClient(config, grainFactory);
                        }
                        outsideRuntimeClient = runtimeClient;  // Keep reference, to avoid GC problems
                        outsideRuntimeClient.Start();
         
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

            if (RuntimeClient.Current != null)
            {
                try
                {
                    RuntimeClient.Current.Reset(cleanup);
                }
                catch (Exception) { }

                RuntimeClient.Current = null;
            }
            outsideRuntimeClient = null;
            grainFactory = null;
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
                return RuntimeClient.Current.AppLogger;
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
            RuntimeClient.Current.SetResponseTimeout(timeout);
        }

        /// <summary>
        /// Get a timeout of responses on this Orleans client.
        /// </summary>
        /// <returns>The response timeout.</returns>
        /// <exception cref="InvalidOperationException">Thrown if Orleans runtime is not correctly initialized before this call.</exception>
        public static TimeSpan GetResponseTimeout()
        {
            CheckInitialized();
            return RuntimeClient.Current.GetResponseTimeout();
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
        public static Action<InvokeMethodRequest, IGrain> ClientInvokeCallback { get; set; }

        public static IEnumerable<Streams.IStreamProvider> GetStreamProviders()
        {
            return RuntimeClient.Current.CurrentStreamProviderManager.GetStreamProviders();
        }

        internal static IStreamProviderRuntime CurrentStreamProviderRuntime
        {
            get { return RuntimeClient.Current.CurrentStreamProviderRuntime; }
        }

        public static Streams.IStreamProvider GetStreamProvider(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException("name");
            return RuntimeClient.Current.CurrentStreamProviderManager.GetProvider(name) as Streams.IStreamProvider;
        }

        internal static IList<Uri> Gateways
        {
            get
            {
                CheckInitialized();
                return outsideRuntimeClient.Gateways;
            }
        }
    }
}
