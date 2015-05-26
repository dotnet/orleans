/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace Orleans
{
    /// <summary>
    /// Client runtime for connecting to Orleans system
    /// </summary>
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
            CurrentConfig = tcs.Task.Result; // Wait for Init to finish
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

                        ClientProviderRuntime.InitializeSingleton();

                        if (runtimeClient == null)
                        {
                            runtimeClient = new OutsideRuntimeClient(config);
                        }
                        outsideRuntimeClient = runtimeClient;  // Keep reference, to avoid GC problems
                        outsideRuntimeClient.Start();

                        LimitManager.Initialize(config);

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
        /// This is the lock free version of uninitilize so we can share 
        /// it between the public method and error paths inside initialize.
        /// This should only be called inside a lock(initLock) block.
        /// </summary>
        private static void InternalUninitialize()
        {
            // Update this first so IsInitialized immediately begins returning
            // false.  Since this method should be protected externally by 
            // a lock(initLock) we should be able to reset everything else 
            // before the next init attempt.
            isFullyInitialized = false;

            LimitManager.UnInitialize();
            
            if (RuntimeClient.Current != null)
            {
                try
                {
                    RuntimeClient.Current.Reset();
                }
                catch (Exception) { }

                RuntimeClient.Current = null;

                try
                {
                    ClientProviderRuntime.UninitializeSingleton();
                }
                catch (Exception) { }
            }
            outsideRuntimeClient = null;

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
        /// </summary>
        /// <remarks>This callback method should return promptly and do a minimum of work, to avoid blocking calling thread or impacting throughput.</remarks>
        /// <param name="request">Details of the method to be invoked, including InterfaceId and MethodId</param>
        /// <param name="grain">The GrainReference this request is being sent through.</param>
        public static Action<InvokeMethodRequest, IGrain> ClientInvokeCallback { get; set; }

        public static IEnumerable<Streams.IStreamProvider> GetStreamProviders()
        {
            return RuntimeClient.Current.CurrentStreamProviderManager.GetStreamProviders();
        }

        public static Streams.IStreamProvider GetStreamProvider(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentNullException("name");
            return RuntimeClient.Current.CurrentStreamProviderManager.GetProvider(name) as Streams.IStreamProvider;
        }

        internal static List<Uri> Gateways
        {
            get
            {
                CheckInitialized();
                return outsideRuntimeClient.Gateways;
            }
        }

        internal static MethodInfo GetStaticMethodThroughReflection(string assemblyName, string className, string methodName, Type[] argumentTypes)
        {
            var asm = Assembly.Load(assemblyName);
            if (asm == null) 
                throw new InvalidOperationException(string.Format("Cannot find assembly {0}", assemblyName));

            var cl = asm.GetType(className);
            if (cl == null) 
                throw new InvalidOperationException(string.Format("Cannot find class {0} in assembly {1}", className, assemblyName));

            MethodInfo method;
            method = argumentTypes == null 
                ? cl.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) 
                : cl.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, argumentTypes, null);
            
            if (method == null) 
                throw new InvalidOperationException(string.Format("Cannot find static method {0} of class {1} in assembly {2}", methodName, className, assemblyName));

            return method;
        }

        internal static object InvokeStaticMethodThroughReflection(string assemblyName, string className, string methodName, Type[] argumentTypes, object[] arguments)
        {
            var method = GetStaticMethodThroughReflection(assemblyName, className, methodName, argumentTypes);
            return method.Invoke(null, arguments);
        }

        internal static Type LoadTypeThroughReflection(string assemblyName, string className)
        {
            var asm = Assembly.Load(assemblyName);
            if (asm == null) throw new InvalidOperationException(string.Format("Cannot find assembly {0}", assemblyName));

            var cl = asm.GetType(className);
            if (cl == null) throw new InvalidOperationException(string.Format("Cannot find class {0} in assembly {1}", className, assemblyName));

            return cl;
        }
    }
}
