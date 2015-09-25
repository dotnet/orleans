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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Remoting;
using System.Threading.Tasks;
using Orleans.Core;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;
using Orleans.TestingHost.Extensions;

namespace Orleans.TestingHost
{
    using Orleans.CodeGeneration;

    /// <summary>
    /// A host class for local testing with Orleans using in-process silos. 
    /// 
    /// Runs a Primary and Secondary silo in seperate app domains, and client in the main app domain.
    /// Additional silos can also be started in-process if required for particular test cases.
    /// </summary>
    /// <remarks>
    /// Make sure the following files are included in any test projects that use <c>TestingSiloHost</c>, 
    /// and ensure "Copy if Newer" is set to ensure the config files are included in the test set.
    /// <code>
    /// OrleansConfigurationForTesting.xml
    /// ClientConfigurationForTesting.xml
    /// </code>
    /// Also make sure that your test project references your test grains and test grain interfaces 
    /// projects, and has CopyLocal=True set on those references [which should be the default].
    /// </remarks>
    public abstract class TestingSiloHost
    {
        protected static SiloHandle Primary = null;
        protected static SiloHandle Secondary = null;
        protected static readonly List<SiloHandle> additionalSilos = new List<SiloHandle>();
        protected static readonly Dictionary<string, byte[]> additionalAssemblies = new Dictionary<string, byte[]>();

        protected readonly TestingSiloOptions siloInitOptions;
        protected readonly TestingClientOptions clientInitOptions;

        private static TimeSpan _livenessStabilizationTime;

        protected static readonly Random random = new Random();

        public static string DeploymentId = null;
        public static string DeploymentIdPrefix = null;

        public const int BasePort = 22222;
        public const int ProxyBasePort = 40000;

        private static int InstanceCounter = 0;

        public IGrainFactory GrainFactory { get; private set; }

        public Logger logger 
        {
            get { return GrainClient.Logger; }
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the default silo config options.
        /// </summary>
        protected TestingSiloHost()
            : this(new TestingSiloOptions(), new TestingClientOptions())
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// ensuring that fresh silos are started if they were already running.
        /// </summary>
        protected TestingSiloHost(bool startFreshOrleans)
            : this(new TestingSiloOptions { StartFreshOrleans = startFreshOrleans }, new TestingClientOptions())
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo config options.
        /// </summary>
        protected TestingSiloHost(TestingSiloOptions siloOptions)
            : this(siloOptions, new TestingClientOptions())
        {
        }

        /// <summary>
        /// Start the default Primary and Secondary test silos, plus client in-process, 
        /// using the specified silo and client config options.
        /// </summary>
        protected TestingSiloHost(TestingSiloOptions siloOptions, TestingClientOptions clientOptions)
        {
            siloInitOptions = siloOptions;
            clientInitOptions = clientOptions;

            AppDomain.CurrentDomain.UnhandledException += ReportUnobservedException;

            try
            {
                InitializeAsync(siloOptions, clientOptions).Wait();
                string startMsg = "----------------------------- STARTING NEW UNIT TEST SILO HOST: " + GetType().FullName + " -------------------------------------";
                WriteLog(startMsg);
            }
            catch (TimeoutException te)
            {
                throw new TimeoutException("Timeout during test initialization", te);
            }
            catch (Exception ex)
            {
                Exception baseExc = ex.GetBaseException();
                if (baseExc is TimeoutException)
                {
                    throw new TimeoutException("Timeout during test initialization", ex);
                }
                // IMPORTANT:
                // Do NOT re-throw the original exception here, also not as an internal exception inside AggregateException
                // Due to the way MS tests works, if the original exception is an Orleans exception,
                // it's assembly might not be loaded yet in this phase of the test.
                // As a result, we will get "MSTest: Unit Test Adapter threw exception: Type is not resolved for member XXX"
                // and will loose the oroginal exception. This makes debugging tests super hard!
                // The root cause has to do with us initializing our tests from Test constructor and not from TestInitialize method.
                // More details: http://dobrzanski.net/2010/09/20/mstest-unit-test-adapter-threw-exception-type-is-not-resolved-for-member/
                throw new Exception(
                    string.Format("Exception during test initialization: {0}",
                        TraceLogger.PrintException(baseExc)));
            }
        }

        /// <summary>
        /// Get the list of current active silos.
        /// </summary>
        /// <returns>List of current silos.</returns>
        public IEnumerable<SiloHandle> GetActiveSilos()
        {
            WriteLog("GetActiveSilos: Primary={0} Secondary={1} + {2} Additional={3}",
                Primary, Secondary, additionalSilos.Count, additionalSilos);

            if (null != Primary && Primary.Silo != null) yield return Primary;
            if (null != Secondary && Secondary.Silo != null) yield return Secondary;
            if (additionalSilos.Count > 0)
                foreach (var s in additionalSilos)
                    if (null != s && s.Silo != null)
                        yield return s;
        }

        /// <summary>
        /// Find the silo handle for the specified silo address.
        /// </summary>
        /// <param name="siloAddress">Silo address to be found.</param>
        /// <returns>SiloHandle of the appropriate silo, or <c>null</c> if not found.</returns>
        public SiloHandle GetSiloForAddress(SiloAddress siloAddress)
        {
            List<SiloHandle> activeSilos = GetActiveSilos().ToList();
            var ret = activeSilos.Where(s => s.Silo.SiloAddress.Equals(siloAddress)).FirstOrDefault();
            return ret;
        }

        /// <summary>
        /// Wait for the silo liveness sub-system to detect and act on any recent cluster membership changes.
        /// </summary>
        /// <param name="didKill">Whether recent membership changes we done by graceful Stop.</param>
        public async Task WaitForLivenessToStabilizeAsync(bool didKill = false)
        {
            TimeSpan stabilizationTime = _livenessStabilizationTime;
            WriteLog(Environment.NewLine + Environment.NewLine + "WaitForLivenessToStabilize is about to sleep for {0}", stabilizationTime);
            await Task.Delay(stabilizationTime);           
            WriteLog("WaitForLivenessToStabilize is done sleeping");
        }

        private static TimeSpan GetLivenessStabilizationTime(GlobalConfiguration global, bool didKill = false)
        {
            TimeSpan stabilizationTime = TimeSpan.Zero;
            if (didKill)
            {
                // in case of hard kill (kill and not Stop), we should give silos time to detect failures first.
                stabilizationTime = TestingUtils.Multiply(global.ProbeTimeout, global.NumMissedProbesLimit);
            }
            if (global.UseLivenessGossip)
            {
                stabilizationTime += TimeSpan.FromSeconds(5);
            }
            else
            {
                stabilizationTime += TestingUtils.Multiply(global.TableRefreshTimeout, 2);
            }
            return stabilizationTime;
        }

        /// <summary>
        /// Start an additional silo, so that it joins the existing cluster with the default Primary and Secondary silos.
        /// </summary>
        /// <returns>SiloHandle for the newly started silo.</returns>
        public SiloHandle StartAdditionalSilo()
        {
            SiloHandle instance = StartOrleansSilo(
                Silo.SiloType.Secondary,
                siloInitOptions,
                InstanceCounter++);
            additionalSilos.Add(instance);
            return instance;
        }

        /// <summary>
        /// Start a number of additional silo, so that they join the existing cluster with the default Primary and Secondary silos.
        /// </summary>
        /// <param name="numExtraSilos">Number of additional silos to start.</param>
        /// <returns>List of SiloHandles for the newly started silos.</returns>
        public List<SiloHandle> StartAdditionalSilos(int numExtraSilos)
        {
            List<SiloHandle> instances = new List<SiloHandle>();
            for (int i = 0; i < numExtraSilos; i++)
            {
                SiloHandle instance = StartAdditionalSilo();
                instances.Add(instance);
            }
            return instances;
        }

        /// <summary>
        /// Stop any additional silos, not including the default Primary and Secondary silos.
        /// </summary>
        public static void StopAdditionalSilos()
        {
            foreach (SiloHandle instance in additionalSilos)
            {
                StopSilo(instance);
            }
            additionalSilos.Clear();
        }

        /// <summary>
        /// Stop the default Primary and Secondary silos.
        /// </summary>
        public static void StopDefaultSilos()
        {
            try
            {
                GrainClient.Uninitialize();
            }
            catch (Exception exc) { WriteLog("Exception Uninitializing grain client: {0}", exc); }

            StopSilo(Secondary);
            StopSilo(Primary);
            Secondary = null;
            Primary = null;
            InstanceCounter = 0;
            DeploymentId = null;
        }

        /// <summary>
        /// Stop all current silos.
        /// </summary>
        public static void StopAllSilos()
        {
            StopAdditionalSilos();
            StopDefaultSilos();
        }

        /// <summary>
        /// Restart the default Primary and Secondary silos.
        /// </summary>
        public void RestartDefaultSilos()
        {
            TestingSiloOptions primarySiloOptions = Primary.Options;
            TestingSiloOptions secondarySiloOptions = Secondary.Options;
            // Restart as the same deployment
            string deploymentId = DeploymentId;

            StopDefaultSilos();

            DeploymentId = deploymentId;
            primarySiloOptions.PickNewDeploymentId = false;
            secondarySiloOptions.PickNewDeploymentId = false;

            Primary = StartOrleansSilo(Silo.SiloType.Primary, primarySiloOptions, InstanceCounter++);
            Secondary = StartOrleansSilo(Silo.SiloType.Secondary, secondarySiloOptions, InstanceCounter++);
            WaitForLivenessToStabilizeAsync().Wait();
            GrainClient.Initialize();
        }

        /// <summary>
        /// Start a Secondary silo with a given instanceCounter 
        /// (allows to set the port number as before or new, depending on the scenario).
        /// </summary>
        public void StartSecondarySilo(TestingSiloOptions secondarySiloOptions, int instanceCounter)
        {
            secondarySiloOptions.PickNewDeploymentId = false;
            Secondary = StartOrleansSilo(Silo.SiloType.Secondary, secondarySiloOptions, instanceCounter);
        }

        /// <summary>
        /// Do a semi-graceful Stop of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be stopped.</param>
        public static void StopSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                StopOrleansSilo(instance, true);
            }
        }

        /// <summary>
        /// Do an immediate Kill of the specified silo.
        /// </summary>
        /// <param name="instance">Silo to be killed.</param>
        public static void KillSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                // do NOT stop, just kill directly, to simulate crash.
                StopOrleansSilo(instance, false);
            }
        }

        /// <summary>
        /// Do a Stop or Kill of the specified silo, followed by a restart.
        /// </summary>
        /// <param name="instance">Silo to be restarted.</param>
        public SiloHandle RestartSilo(SiloHandle instance)
        {
            if (instance != null)
            {
                var options = instance.Options;
                var type = instance.Silo.Type;
                StopOrleansSilo(instance, true);
                instance = StartOrleansSilo(type, options, InstanceCounter++);
                return instance;
            }
            return null;
        }

        public virtual void AdjustForTest(ClusterConfiguration config)
        {
            config.AdjustForTestEnvironment();
        }

        public virtual void AdjustForTest(ClientConfiguration config)
        {
            config.AdjustForTestEnvironment();
        }

        #region Private methods

        /// <summary>
        /// Imports assemblies generated by runtime code generation from the provided silo.
        /// </summary>
        /// <param name="siloHandle">The silo.</param>
        private static void ImportGeneratedAssemblies(SiloHandle siloHandle)
        {
            var silo = siloHandle.Silo;
            if (silo != null && silo.TestHookup != null)
            {
                var generatedAssemblies = new Silo.TestHookups.GeneratedAssemblies();
                silo.TestHookup.UpdateGeneratedAssemblies(generatedAssemblies);
                foreach (var assembly in generatedAssemblies.Assemblies)
                {
                    // If we have never seen generated code for this assembly before, or generated code might be
                    // newer, store it for later silo creation.
                    byte[] existing;
                    if (!additionalAssemblies.TryGetValue(assembly.Key, out existing) || assembly.Value != null)
                    {
                        additionalAssemblies[assembly.Key] = assembly.Value;
                    }
                }
            }
        }

        private async Task InitializeAsync(TestingSiloOptions options, TestingClientOptions clientOptions)
        {
            bool doStartPrimary = false;
            bool doStartSecondary = false;

            if (options.StartFreshOrleans)
            {
                // the previous test was !startFresh, so we need to cleanup after it.
                if (Primary != null || Secondary != null || GrainClient.IsInitialized)
                {
                    StopDefaultSilos();
                }

                StopAdditionalSilos();

                if (options.StartPrimary)
                {
                    doStartPrimary = true;
                }
                if (options.StartSecondary)
                {
                    doStartSecondary = true;
                }
            }
            else
            {
                if (options.StartPrimary && Primary == null)
                {
                    // first time.
                    doStartPrimary = true;
                }
                if (options.StartSecondary && Secondary == null)
                {
                    doStartSecondary = true;
                }
            }
            if (options.PickNewDeploymentId && String.IsNullOrEmpty(DeploymentId))
            {
                DeploymentId = GetDeploymentId();
            }

            if (options.ParallelStart)
            {
                var handles = new List<Task<SiloHandle>>();     
                if (doStartPrimary)
                {
                    int instanceCount = InstanceCounter++;
                    handles.Add(Task.Run(() => StartOrleansSilo(Silo.SiloType.Primary, options, instanceCount)));
                }
                if (doStartSecondary)
                {
                    int instanceCount = InstanceCounter++;
                    handles.Add(Task.Run(() => StartOrleansSilo(Silo.SiloType.Secondary, options, instanceCount)));
                }
                await Task.WhenAll(handles.ToArray());
                if (doStartPrimary)
                {
                    Primary = await handles[0];
                }
                if (doStartSecondary)
                {
                    Secondary = await handles[1];
                }
            }else
            {
                if (doStartPrimary)
                {
                    Primary = StartOrleansSilo(Silo.SiloType.Primary, options, InstanceCounter++);
                }
                if (doStartSecondary)
                {
                    Secondary = StartOrleansSilo(Silo.SiloType.Secondary, options, InstanceCounter++);
                }
            }

            if (!GrainClient.IsInitialized && options.StartClient)
            {
                ClientConfiguration clientConfig;
                if (clientOptions.ClientConfigFile != null)
                {
                    clientConfig = ClientConfiguration.LoadFromFile(clientOptions.ClientConfigFile.FullName);
                }
                else
                {
                    clientConfig = ClientConfiguration.StandardLoad();
                }
                if (clientOptions.ProxiedGateway && clientOptions.Gateways != null)
                {
                    clientConfig.Gateways = clientOptions.Gateways;
                    if (clientOptions.PreferedGatewayIndex >= 0)
                        clientConfig.PreferedGatewayIndex = clientOptions.PreferedGatewayIndex;
                }
                if (clientOptions.PropagateActivityId)
                {
                    clientConfig.PropagateActivityId = clientOptions.PropagateActivityId;
                }
                if (!String.IsNullOrEmpty(DeploymentId))
                {
                    clientConfig.DeploymentId = DeploymentId;
                }
                if (Debugger.IsAttached)
                {
                    // Test is running inside debugger - Make timeout ~= infinite
                    clientConfig.ResponseTimeout = TimeSpan.FromMilliseconds(1000000);
                }
                else if (clientOptions.ResponseTimeout > TimeSpan.Zero)
                {
                    clientConfig.ResponseTimeout = clientOptions.ResponseTimeout;
                }
                
                if (options.LargeMessageWarningThreshold > 0)
                {
                    clientConfig.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;
                }
                AdjustForTest(clientConfig);

                GrainClient.Initialize(clientConfig);
                GrainFactory = GrainClient.GrainFactory;
            }
        }

        private SiloHandle StartOrleansSilo(Silo.SiloType type, TestingSiloOptions options, int instanceCount, AppDomain shared = null)
        {
            // Load initial config settings, then apply some overrides below.
            ClusterConfiguration config = new ClusterConfiguration();
            if (options.SiloConfigFile == null)
            {
                config.StandardLoad();
            }
            else
            {
                config.LoadFromFile(options.SiloConfigFile.FullName);
            }

            int basePort = options.BasePort < 0 ? BasePort : options.BasePort;


            if (config.Globals.SeedNodes.Count > 0 && options.BasePort < 0)
            {
                config.PrimaryNode = config.Globals.SeedNodes[0];
            }
            else
            {
                config.PrimaryNode = new IPEndPoint(IPAddress.Loopback, basePort);
            }
            config.Globals.SeedNodes.Clear();
            config.Globals.SeedNodes.Add(config.PrimaryNode);

            if (!String.IsNullOrEmpty(DeploymentId))
            {
                config.Globals.DeploymentId = DeploymentId;
            }
            config.Defaults.PropagateActivityId = options.PropagateActivityId;
            if (options.LargeMessageWarningThreshold > 0)
            {
                config.Defaults.LargeMessageWarningThreshold = options.LargeMessageWarningThreshold;
            }

            config.Globals.LivenessType = options.LivenessType;
            config.Globals.ReminderServiceType = options.ReminderServiceType;
            if (!String.IsNullOrEmpty(options.DataConnectionString))
            {
                config.Globals.DataConnectionString = options.DataConnectionString;
            }

            _livenessStabilizationTime = GetLivenessStabilizationTime(config.Globals);
            
            string siloName;
            switch (type)
            {
                case Silo.SiloType.Primary:
                    siloName = "Primary";
                    break;
                default:
                    siloName = "Secondary_" + instanceCount.ToString(CultureInfo.InvariantCulture);
                    break;
            }

            NodeConfiguration nodeConfig = config.GetConfigurationForNode(siloName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            nodeConfig.Port = basePort + instanceCount;
            nodeConfig.DefaultTraceLevel = config.Defaults.DefaultTraceLevel;
            nodeConfig.PropagateActivityId = config.Defaults.PropagateActivityId;
            nodeConfig.BulkMessageLimit = config.Defaults.BulkMessageLimit;

            if (nodeConfig.ProxyGatewayEndpoint != null && nodeConfig.ProxyGatewayEndpoint.Address != null)
            {
                nodeConfig.ProxyGatewayEndpoint = new IPEndPoint(nodeConfig.ProxyGatewayEndpoint.Address, ProxyBasePort + instanceCount);
            }

            config.Globals.ExpectedClusterSize = 2;

            config.Overrides[siloName] = nodeConfig;

            AdjustForTest(config);

            WriteLog("Starting a new silo in app domain {0} with config {1}", siloName, config.ToString(siloName));
            AppDomain appDomain;
            Silo silo = LoadSiloInNewAppDomain(siloName, type, config, out appDomain);

            silo.Start();

            SiloHandle retValue = new SiloHandle
            {
                Name = siloName,
                Silo = silo,
                Options = options,
                Endpoint = silo.SiloAddress.Endpoint,
                AppDomain = appDomain,
            };
            ImportGeneratedAssemblies(retValue);
            return retValue;
        }

        private static void StopOrleansSilo(SiloHandle instance, bool stopGracefully)
        {
            var silo = instance.Silo;
            if (stopGracefully)
            {
                try
                {
                    if (silo != null)
                    {
                        silo.Shutdown();
                    }
                }
                catch (RemotingException re)
                {
                    Console.WriteLine(re); /* Ignore error */
                }
                catch (Exception exc)
                {
                    Console.WriteLine(exc);
                    throw;
                }
            }

            ImportGeneratedAssemblies(instance);

            try
            {
                UnloadSiloInAppDomain(instance.AppDomain);
            }
            catch (Exception exc)
            {
                Console.WriteLine(exc);
                throw;
            }

            instance.AppDomain = null;
            instance.Silo = null;
            instance.Process = null;
        }

        private static Silo LoadSiloInNewAppDomain(string siloName, Silo.SiloType type, ClusterConfiguration config, out AppDomain appDomain)
        {
            AppDomainSetup setup = GetAppDomainSetupInfo();

            appDomain = AppDomain.CreateDomain(siloName, null, setup);

            // Load each of the additional assemblies.
            Silo.TestHookups.CodeGeneratorOptimizer optimizer = null;
            foreach (var assembly in additionalAssemblies)
            {
                if (optimizer == null)
                {
                    optimizer =
                        (Silo.TestHookups.CodeGeneratorOptimizer)
                        appDomain.CreateInstanceFromAndUnwrap(
                            "OrleansRuntime.dll",
                            typeof(Silo.TestHookups.CodeGeneratorOptimizer).FullName,
                            false,
                            BindingFlags.Default,
                            null,
                            null,
                            CultureInfo.CurrentCulture,
                            new object[] { });
                }

                if (assembly.Value != null)
                {
                    optimizer.AddCachedAssembly(assembly.Key, assembly.Value);
                }
            }

            var args = new object[] { siloName, type, config };

            var silo = (Silo)appDomain.CreateInstanceFromAndUnwrap(
                "OrleansRuntime.dll", typeof(Silo).FullName, false,
                BindingFlags.Default, null, args, CultureInfo.CurrentCulture,
                new object[] { });
            
            appDomain.UnhandledException += ReportUnobservedException;
            
            return silo;
        }

        private static void UnloadSiloInAppDomain(AppDomain appDomain)
        {
            if (appDomain != null)
            {
                appDomain.UnhandledException -= ReportUnobservedException;
                AppDomain.Unload(appDomain);
            }
        }

        private static AppDomainSetup GetAppDomainSetupInfo()
        {
            AppDomain currentAppDomain = AppDomain.CurrentDomain;

            return new AppDomainSetup
            {
                ApplicationBase = Environment.CurrentDirectory,
                ConfigurationFile = currentAppDomain.SetupInformation.ConfigurationFile,
                ShadowCopyFiles = currentAppDomain.SetupInformation.ShadowCopyFiles,
                ShadowCopyDirectories = currentAppDomain.SetupInformation.ShadowCopyDirectories,
                CachePath = currentAppDomain.SetupInformation.CachePath
            };
        }

        private string GetDeploymentId()
        {
            if (!String.IsNullOrEmpty(DeploymentId))
            {
                return DeploymentId;
            }
            string prefix = DeploymentIdPrefix ?? "testdepid-";
            int randomSuffix = random.Next(1000);
            DateTime now = DateTime.UtcNow;
            string DateTimeFormat = "yyyy-MM-dd-hh-mm-ss-fff";
            string depId = String.Format("{0}{1}-{2}",
                prefix, now.ToString(DateTimeFormat, CultureInfo.InvariantCulture), randomSuffix);
            return depId;
        }

        private static void ReportUnobservedException(object sender, UnhandledExceptionEventArgs eventArgs)
        {
            Exception exception = (Exception)eventArgs.ExceptionObject;
            Console.WriteLine("Unobserved exception: {0}", exception);
        }

        public static void WriteLog(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }

        #endregion
    }
}
