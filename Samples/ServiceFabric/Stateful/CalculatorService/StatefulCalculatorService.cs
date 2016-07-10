using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Orleans.ServiceFabric;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace StatefulCalculatorService
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.ServiceFabric.Data;

    /// <summary>
    /// An instance of this class is created for each service replica by the Service Fabric runtime.
    /// </summary>
    internal sealed class StatefulCalculatorService : StatefulService
    {
        public StatefulCalculatorService(StatefulServiceContext context)
            : base(context)
        { }

        /// <summary>
        /// Optional override to create listeners (e.g., HTTP, Service Remoting, WCF, etc.) for this service replica to handle client or user requests.
        /// </summary>
        /// <remarks>
        /// For more information on service communication, see http://aka.ms/servicefabricservicecommunication
        /// </remarks>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new[] { OrleansServiceListener.CreateStateful(this.GetClusterConfiguration(), this.Partition), };
        }

        /// <summary>
        /// This is the main entry point for your service replica.
        /// This method executes when this replica of your service becomes primary and has write status.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service replica.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following sample code with your own logic 
            //       or remove this RunAsync override if it's not needed in your service.

            var myDictionary = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, long>>("myDictionary");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

               /* using (var tx = this.StateManager.CreateTransaction())
                {
                    var result = await myDictionary.TryGetValueAsync(tx, "Counter");

                    ServiceEventSource.Current.ServiceMessage(this, "Current Counter Value: {0}",
                        result.HasValue ? result.Value.ToString() : "Value does not exist.");

                    await myDictionary.AddOrUpdateAsync(tx, "Counter", 0, (key, value) => ++value);

                    // If an exception is thrown before calling CommitAsync, the transaction aborts, all changes are 
                    // discarded, and nothing is saved to the secondary replicas.
                    await tx.CommitAsync();
                }*/

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        public ClusterConfiguration GetClusterConfiguration()
        {
            var config = new ClusterConfiguration();

            // Configure logging and metrics collection.
            config.Defaults.TraceFileName = null;
            config.Defaults.TraceFilePattern = null;
            config.Defaults.StatisticsCollectionLevel = StatisticsLevel.Info;
            config.Defaults.StatisticsLogWriteInterval = TimeSpan.FromDays(6);
            config.Defaults.TurnWarningLengthThreshold = TimeSpan.FromSeconds(15);
            config.Defaults.TraceToConsole = true;
            config.Defaults.DefaultTraceLevel = Severity.Info;

            // Configure providers
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;
            config.Globals.LivenessType = GlobalConfiguration.LivenessProviderType.Custom;
            config.Globals.MembershipTableAssembly = typeof(ServiceFabricNamingServiceGatewayProvider).Assembly.FullName;
            config.Globals.DataConnectionString = this.Context.ServiceName.ToString();
            config.Defaults.ServiceProviderBuilder += this.BuildServiceProvider;
            config.Globals.RegisterBootstrapProvider<TestBootstrapProvider>("Test");
            config.Globals.RegisterStorageProvider<ReliableDictionaryStateProvider>("fabric");
            
            config.Globals.ResponseTimeout = TimeSpan.FromSeconds(90);

            return config;
        }

        private IServiceProvider BuildServiceProvider(IServiceCollection serviceCollection)
        {
            return serviceCollection.AddServiceFabricSupport(this).BuildServiceProvider();
        }
    }
}
