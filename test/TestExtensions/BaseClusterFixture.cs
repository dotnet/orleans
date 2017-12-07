using System;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.TestingHost;

namespace TestExtensions
{
    public abstract class BaseTestClusterFixture : IDisposable
    {
        private ExceptionDispatchInfo preconditionsException;
        private ILogger appLogger;
        static BaseTestClusterFixture()
        {
            TestDefaultConfiguration.InitializeDefaults();
        }

        protected BaseTestClusterFixture()
        {
            try
            {
                CheckPreconditionsOrThrow();
            }
            catch (Exception ex)
            {
                preconditionsException = ExceptionDispatchInfo.Capture(ex);
                return;
            }

            var testCluster = CreateTestCluster();
            if (testCluster?.Primary == null)
            {
                testCluster?.Deploy();
            }
            this.HostedCluster = testCluster;
            this.appLogger = this.Client?.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Application");
        }

        public void EnsurePreconditionsMet()
        {
            preconditionsException?.Throw();
        }

        protected virtual void CheckPreconditionsOrThrow() { }


        protected abstract TestCluster CreateTestCluster();

        public TestCluster HostedCluster { get; }

        public IGrainFactory GrainFactory => this.HostedCluster?.GrainFactory;

        public IClusterClient Client => this.HostedCluster?.Client;

        public ILogger Logger => this.appLogger;
           

        public virtual void Dispose()
        {
            this.HostedCluster?.StopAllSilos();
        }
    }
}