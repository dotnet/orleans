using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;

namespace UnitTests.Tester
{
    /// <summary>
    /// Base class that ensures a silo cluster is started with the default configuration, and avoids restarting it if the previous test used the same default base.
    /// </summary>
    [TestClass]
    public abstract class HostedTestClusterEnsureDefaultStarted : OrleansTestingBase
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

    /// <summary>
    /// Base class that ensures a silo cluster is started per fixture, and avoids restarting it if the previous test was in the same fixture.
    /// It searches for a public static CreateSiloHost method that returns a <see cref="TestingSiloHost"/> in the concrete TestClass to new up the cluster once.
    /// </summary>
    [TestClass]
    public abstract class HostedTestClusterPerFixture : OrleansTestingBase
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
            var siloHostFactory = IsolatedHostedTestClusterUtils.FindTestingSiloHostFactory(this);
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

    /// <summary>
    /// Base class that ensures a silo cluster is started per test.
    /// It searches for a public static CreateSiloHost method that returns a <see cref="TestingSiloHost"/> in the concrete TestClass to new up the cluster.
    /// </summary>
    [TestClass]
    public abstract class HostedTestClusterPerTest : OrleansTestingBase
    {
        protected TestingSiloHost HostedCluster { get; private set; }

        [TestInitialize]
        public void InitializeOrleansHost()
        {
            TestingSiloHost.StopAllSilosIfRunning();
            var siloHostFactory = IsolatedHostedTestClusterUtils.FindTestingSiloHostFactory(this);
            this.HostedCluster = siloHostFactory.Invoke();
        }

        [TestCleanup]
        public void StopOrleansAfterTest()
        {
            TestingSiloHost.StopAllSilosIfRunning();
        }
    }

    internal static class IsolatedHostedTestClusterUtils
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
}
