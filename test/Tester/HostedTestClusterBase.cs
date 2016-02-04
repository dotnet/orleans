using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Orleans.TestingHost;
using Tester;
using Xunit;

namespace UnitTests.Tester
{
    /// <summary>
    /// Base class that ensures a silo cluster is started with the default configuration, and avoids restarting it if the previous test used the same default base.
    /// </summary>
    [Collection("DefaultCluster")]
    public abstract class HostedTestClusterEnsureDefaultStarted : OrleansTestingBase
    {
        protected TestingSiloHost HostedCluster { get; private set; }

        public HostedTestClusterEnsureDefaultStarted(DefaultClusterFixture fixture)
        {
            this.HostedCluster = fixture.HostedCluster;
        }

        public HostedTestClusterEnsureDefaultStarted()
        {

        }
    }

    public abstract class HostedTestClusterPerTest : OrleansTestingBase, IDisposable
    {
        protected TestingSiloHost HostedCluster { get; private set; }

        public HostedTestClusterPerTest()
        {
            this.HostedCluster = this.CreateSiloHost();
        }

        public virtual TestingSiloHost CreateSiloHost()
        {
            return new TestingSiloHost(true);
        }

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}
