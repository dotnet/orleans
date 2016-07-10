using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Orleans.ServiceFabric;

namespace CalculatorService
{
    using System.Diagnostics;

    using Actors;
    
    using Orleans.Runtime;
    using Orleans.Runtime.Configuration;

    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class CalculatorService : StatelessService
    {
        static CalculatorService()
        {
            Trace.TraceInformation($"Ensuring Actor assembly '{typeof(CalculatorGrain).Assembly}' is loaded.");
        }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            var silo =  OrleansServiceListener.CreateStateless(this.GetClusterConfiguration(), this.Partition);
            return new[] { silo, };
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancelServiceInstance">Canceled when Service Fabric terminates this instance.</param>
        protected override async Task RunAsync(CancellationToken cancelServiceInstance)
        {
            // TODO: Replace the following sample code with your own logic.

            int iterations = 0;
            // This service instance continues processing until the instance is terminated.
            while (!cancelServiceInstance.IsCancellationRequested)
            {

                // Log what the service is doing
                ServiceEventSource.Current.ServiceMessage(this, "Working-{0}", iterations++);

                // Pause for 1 second before continue processing.
                await Task.Delay(TimeSpan.FromSeconds(1), cancelServiceInstance);
            }
        }

        public ClusterConfiguration GetClusterConfiguration()
        {
            var config = new ClusterConfiguration();

            // Configure logging and metrics collection.
            //config.Defaults.StartupTypeName = typeof(SiloServiceLocator).AssemblyQualifiedName;
            config.Defaults.TraceFileName = null;
            config.Defaults.TraceFilePattern = null;
            config.Defaults.StatisticsCollectionLevel = StatisticsLevel.Info;
            config.Defaults.StatisticsLogWriteInterval = TimeSpan.FromDays(6);
            config.Defaults.TurnWarningLengthThreshold = TimeSpan.FromSeconds(15);
            config.Defaults.TraceToConsole = true;
            config.Defaults.DefaultTraceLevel = Severity.Warning;

            // Configure providers
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.AzureTable;
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.AzureTable;

            //config.Globals.ExpectedClusterSize = nodeList.Count; // An overestimate is tolerable.
            config.Globals.ResponseTimeout = TimeSpan.FromSeconds(90);

            return config;
        }

        public CalculatorService(StatelessServiceContext serviceContext) : base(serviceContext)
        {
        }
    }
}