using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace UnitTests.Tester
{
    /// <summary>
    /// Keep this class as a bridge to the OrleansTestingSilo package, 
    /// because it gives a convenient place to declare all the additional
    /// deployment items required by tests 
    /// - such as the TestGrain assemblies, the client and server config files.
    /// </summary>
    [DeploymentItem("OrleansConfigurationForTesting.xml")]
    [DeploymentItem("ClientConfigurationForTesting.xml")]
    [DeploymentItem("TestGrainInterfaces.dll")]
    [DeploymentItem("TestGrains.dll")]
    [DeploymentItem("OrleansCodeGenerator.dll")]
    [DeploymentItem("OrleansProviders.dll")]
    [DeploymentItem("TestInternalGrainInterfaces.dll")]
    [DeploymentItem("TestInternalGrains.dll")]
    public abstract class UnitTestSiloHost
    {
        protected static readonly Random random = new Random();

        public Logger logger
        {
            get { return GrainClient.Logger; }
        }

        public static void CheckForAzureStorage()
        {
            bool usingLocalWAS = StorageTestConstants.UsingAzureLocalStorageEmulator;

            if (!usingLocalWAS)
            {
                string msg = "Tests are using Azure Cloud Storage, not local WAS emulator.";
                Console.WriteLine(msg);
                return;
            }

            //Starts the storage emulator if not started already and it exists (i.e. is installed).
            if (!StorageEmulator.TryStart())
            {
                string errorMsg = "Azure Storage Emulator could not be started.";
                Console.WriteLine(errorMsg);
                Assert.Inconclusive(errorMsg);
            }
        }

        protected static IGrainFactory GrainFactory { get { return GrainClient.GrainFactory; } }

        public static string DumpTestContext(TestContext context)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendFormat(@"TestName={0}", context.TestName).AppendLine();
            sb.AppendFormat(@"FullyQualifiedTestClassName={0}", context.FullyQualifiedTestClassName).AppendLine();
            sb.AppendFormat(@"CurrentTestOutcome={0}", context.CurrentTestOutcome).AppendLine();
            sb.AppendFormat(@"DeploymentDirectory={0}", context.DeploymentDirectory).AppendLine();
            sb.AppendFormat(@"TestRunDirectory={0}", context.TestRunDirectory).AppendLine();
            sb.AppendFormat(@"TestResultsDirectory={0}", context.TestResultsDirectory).AppendLine();
            sb.AppendFormat(@"ResultsDirectory={0}", context.ResultsDirectory).AppendLine();
            sb.AppendFormat(@"TestRunResultsDirectory={0}", context.TestRunResultsDirectory).AppendLine();
            sb.AppendFormat(@"Properties=[ ");
            foreach (var key in context.Properties.Keys)
            {
                sb.AppendFormat(@"{0}={1} ", key, context.Properties[key]);
            }
            sb.AppendFormat(@" ]").AppendLine();
            return sb.ToString();
        }

        public static long GetRandomGrainId()
        {
            return random.Next();
        }

        public static double CalibrateTimings()
        {
            const int NumLoops = 10000;
            TimeSpan baseline = TimeSpan.FromTicks(80); // Baseline from jthelin03D
            int n;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < NumLoops; i++)
            {
                n = i;
            }
            sw.Stop();
            double multiple = 1.0 * sw.ElapsedTicks / baseline.Ticks;
            Console.WriteLine("CalibrateTimings: {0} [{1} Ticks] vs {2} [{3} Ticks] = x{4}",
                sw.Elapsed, sw.ElapsedTicks,
                baseline, baseline.Ticks,
                multiple);
            return multiple > 1.0 ? multiple : 1.0;
        }

        public static TimeSpan TimeRun(int numIterations, TimeSpan baseline, string what, Action action)
        {
            var stopwatch = new Stopwatch();

            long startMem = GC.GetTotalMemory(true);
            stopwatch.Start();

            action();

            stopwatch.Stop();
            long stopMem = GC.GetTotalMemory(false);
            long memUsed = stopMem - startMem;
            TimeSpan duration = stopwatch.Elapsed;

            string timeDeltaStr = "";
            if (baseline > TimeSpan.Zero)
            {
                double delta = (duration - baseline).TotalMilliseconds / baseline.TotalMilliseconds;
                timeDeltaStr = String.Format("-- Change = {0}%", 100.0 * delta);
            }
            Console.WriteLine("Time for {0} loops doing {1} = {2} {3} Memory used={4}", numIterations, what, duration, timeDeltaStr, memUsed);
            return duration;
        }

        protected void TestSilosStarted(int expected)
        {
            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);

            Dictionary<SiloAddress, SiloStatus> statuses = mgmtGrain.GetHosts(onlyActive: true).Result;
            foreach (var pair in statuses)
            {
                Console.WriteLine("       ######## Silo {0}, status: {1}", pair.Key, pair.Value);
                Assert.AreEqual(
                    SiloStatus.Active,
                    pair.Value,
                    "Failed to confirm start of {0} silos ({1} confirmed).",
                    pair.Value,
                    SiloStatus.Active);
            }
            Assert.AreEqual(expected, statuses.Count);
        }

        public static void ConfigureClientThreadPoolSettingsForStorageTests(int NumDotNetPoolThreads = 200)
        {
            ThreadPool.SetMinThreads(NumDotNetPoolThreads, NumDotNetPoolThreads);
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = NumDotNetPoolThreads; // 1000;
            ServicePointManager.UseNagleAlgorithm = false;
        }

        public static async Task<int> GetActivationCount(string fullTypeName)
        {
            int result = 0;

            IManagementGrain mgmtGrain = GrainClient.GrainFactory.GetGrain<IManagementGrain>(RuntimeInterfaceConstants.SYSTEM_MANAGEMENT_ID);
            SimpleGrainStatistic[] stats = await mgmtGrain.GetSimpleGrainStatistics();
            foreach (var stat in stats)
            {
                if (stat.GrainType == fullTypeName)
                    result += stat.ActivationCount;
            }
            return result;
        }
    }

    [TestClass]
    public abstract class HostedTestClusterEnsureDefaultStarted : UnitTestSiloHost
    {
        private static TestingSiloHost defaultHostedCluster;
        protected TestingSiloHost HostedCluster { get; private set; }

        [TestInitialize]
        public void EnsureDefaultOrleansHostInitialized()
        {

            var runningCluster = TestingSiloHost.Instance;
            if (runningCluster != null && runningCluster == defaultHostedCluster)
            {
                runningCluster.StopAdditionalSilos();
                this.HostedCluster = runningCluster;
                return;
            }

            TestingSiloHost.StopAllSilosIfRunning();
            this.HostedCluster = new TestingSiloHost(true);
            defaultHostedCluster = this.HostedCluster;
        }

        [TestCleanup]
        public void CleanupOrleansBackToDefault()
        {
            if (this.HostedCluster != null && TestingSiloHost.Instance == this.HostedCluster)
            {
                this.HostedCluster.StopAdditionalSilos();
            }
        }
    }

    internal static class IsolatedUnitTestSiloHost
    {
        public static Func<TestingSiloHost> FindTestingSiloHostFactory(object fixture)
        {
            var factory = fixture.GetType()
                .GetTypeInfo()
                .GetMethod("CreateSiloHost", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

            if (factory != null)
            {
                if (!factory.IsStatic)
                {
                    throw new InvalidOperationException("CreateSiloHost must be static");
                }

                if (factory.ReturnParameter == null || factory.ReturnParameter.ParameterType != typeof(TestingSiloHost))
                {
                    throw new InvalidOperationException("CreateSiloHost must have a return type of TestingSiloHost");
                }

                if (factory.GetParameters().Length != 0)
                {
                    throw new InvalidOperationException("CreateSiloHost must not have any input parameters in its signature");
                }

                return () =>
                {
                    try
                    {
                        return (TestingSiloHost)factory.Invoke(fixture, null);
                    }
                    catch (TargetInvocationException ex)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                        return null;
                    }
                };
            }

            Console.WriteLine("Static method named CreateSiloHost was not found in the test fixture type {0}. Using a default silo host.", fixture.GetType().Name);
            var potentialFactory = fixture.GetType()
                .GetTypeInfo()
                .GetMethods()
                .FirstOrDefault(m => m.ReturnParameter != null && m.ReturnParameter.ParameterType == typeof(TestingSiloHost));

            if (potentialFactory != null)
            {
                Console.WriteLine("If you meant to use '{0}' as the factory method, please rename it to 'CreateSiloHost' and follow the method signature guidelines", potentialFactory.Name);
            }

            return () => new TestingSiloHost(true);
        }
    }

    [TestClass]
    public abstract class HostedTestClusterPerFixture : UnitTestSiloHost
    {
        private static TestingSiloHost previousHostedCluster;
        private static string previousFixtureType;
        protected TestingSiloHost HostedCluster { get; private set; }

        [TestInitialize]
        public void EnsureOrleansHostInitialized()
        {
            var fixtureType = this.GetType().AssemblyQualifiedName;
            var runningCluster = TestingSiloHost.Instance;
            if (runningCluster != null
                && previousFixtureType != null 
                && previousFixtureType == fixtureType 
                && runningCluster == previousHostedCluster)
            {
                runningCluster.StopAdditionalSilos();
                this.HostedCluster = runningCluster;
                return;
            }

            previousHostedCluster = null;
            previousFixtureType = null;

            TestingSiloHost.StopAllSilosIfRunning();
            var siloHostFactory = IsolatedUnitTestSiloHost.FindTestingSiloHostFactory(this);
            this.HostedCluster = siloHostFactory.Invoke();
            previousHostedCluster = this.HostedCluster;
            previousFixtureType = fixtureType;
        }

        [TestCleanup]
        public void CleanupOrleansBackToDefault()
        {
            if (this.HostedCluster != null && TestingSiloHost.Instance == this.HostedCluster)
            {
                this.HostedCluster.StopAdditionalSilos();
            }
        }

        // Avoid using ClassCleanup, as the order of execution is not guaranteed, and my run after another test fixture ran.
        //[ClassCleanup]
        //public static void StopOrleansAfterFixture()
        //{
        //    previousHostedCluster = null;
        //    previousFixtureType = null;
        //    TestingSiloHost.StopAllSilosIfRunning();
        //}
    }

    [TestClass]
    public abstract class HostedTestClusterPerTest : UnitTestSiloHost
    {
        protected TestingSiloHost HostedCluster { get; private set; }

        [TestInitialize]
        public void InitializeOrleansHost()
        {
            TestingSiloHost.StopAllSilosIfRunning();
            var siloHostFactory = IsolatedUnitTestSiloHost.FindTestingSiloHostFactory(this);
            this.HostedCluster = siloHostFactory.Invoke();
        }

        [TestCleanup]
        public void StopOrleansAfterTest()
        {
            TestingSiloHost.StopAllSilosIfRunning();
        }
    }
}
