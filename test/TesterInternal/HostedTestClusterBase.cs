using System;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;
using Tester;
using TestExtensions;

namespace UnitTests
{
    using System.Collections.Generic;
    using System.Reflection;

    public abstract class BaseClusterFixture : IDisposable
    {
        protected BaseClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
            GrainClient.Uninitialize();
            SerializationTestEnvironment.Initialize();
            var hostedCluster = CreateClusterHost();
            this.HostedCluster = hostedCluster;
        }

        protected abstract TestingSiloHost CreateClusterHost();

        public TestingSiloHost HostedCluster { get; private set; }

        public virtual void Dispose()
        {
            HostedCluster?.StopAllSilos();
        }
    }
}
