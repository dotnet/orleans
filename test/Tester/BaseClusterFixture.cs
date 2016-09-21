using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Serialization;
using Orleans.TestingHost;

namespace Tester
{
    public abstract class BaseTestClusterFixture : IDisposable
    {
        private static IConfiguration DefaultConfiguration()
        {
            var builder = TestClusterOptions.DefaultConfigurationBuilder();
            builder.AddEnvironmentVariables("Orleans");

            // Try to find a file called OrleansTestSecrets.json up the folder hierarchy, as some of our CI environments are configured this way.
            // There might be some other out-of-the-box way of doing this though.
            var currentDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (currentDir != null && currentDir.Exists)
            {
                string filePath = Path.Combine(currentDir.FullName, "OrleansTestSecrets.json");
                if (File.Exists(filePath))
                {
                    builder.AddJsonFile(filePath);
                    break;
                }
                else
                {
                    currentDir = currentDir.Parent;
                }
            }

            var config = builder.Build();
            return config;
        }

        static BaseTestClusterFixture()
        {
            InitializeDefaults();
        }

        private static int defaultsAreInitialized = 0;
        internal static void InitializeDefaults()
        {
            if (Interlocked.CompareExchange(ref defaultsAreInitialized, 1, 0) == 0)
            {
                TestClusterOptions.DefaultExtendedConfiguration = DefaultConfiguration();
            }
        }

        protected BaseTestClusterFixture()
        {
            GrainClient.Uninitialize();
            SerializationManager.InitializeForTesting();
            var testCluster = CreateTestCluster();
            if (testCluster.Primary == null)
            {
                testCluster.Deploy();
            }
            this.HostedCluster = testCluster;
        }

        protected abstract TestCluster CreateTestCluster();

        public TestCluster HostedCluster { get; private set; }

        public virtual void Dispose()
        {
            this.HostedCluster.StopAllSilos();
        }
    }
}