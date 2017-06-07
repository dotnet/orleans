using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Grains;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Microsoft.Orleans.ServiceFabric;
using Orleans;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;

namespace StatelessCalculatorService
{
    using GrainInterfaces;

    using Microsoft.Extensions.DependencyInjection;

    using Orleans.Providers;

    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    internal sealed class StatelessCalculatorService : StatelessService
    {
        public StatelessCalculatorService(StatelessServiceContext context)
            : base(context)
        {
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            ServiceEventSource.Current.Message($"[PID {Process.GetCurrentProcess().Id}] CreateServiceInstanceListeners()");
            ClusterStartup.Service = this;
            return new[] { OrleansServiceListener.CreateStateless(this.GetClusterConfiguration()) };
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following sample code with your own logic 
            //       or remove this RunAsync override if it's not needed in your service.
            
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }

        public ClusterConfiguration GetClusterConfiguration()
        {
            var config = new ClusterConfiguration();
            config.Globals.ReminderServiceType = GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;
            config.Globals.DataConnectionString = "UseDevelopmentStorage=true";
            config.Globals.RegisterBootstrapProvider<BootstrapProvider>("booter");
            config.Defaults.StartupTypeName = typeof(ClusterStartup).AssemblyQualifiedName;
            LogManager.LogConsumers.Add(new EventSourceLogger());
            return config;
        }

        public class ClusterStartup
        {
            public static StatelessService Service { get; set; }

            public IServiceProvider ConfigureServices(IServiceCollection services)
            {
                services.AddServiceFabricSupport(Service);
                return services.BuildServiceProvider();
            }
        }
    }

    public class EventSourceLogger : ILogConsumer
    {
        private readonly ServiceEventSource eventSource;
        private readonly string pid;

        public EventSourceLogger()
        {
            this.eventSource = ServiceEventSource.Current;
            this.pid = Process.GetCurrentProcess().Id.ToString();
        }

        public void Log(
            Severity severity,
            LoggerType loggerType,
            string caller,
            string message,
            IPEndPoint myIpEndPoint,
            Exception exception,
            int eventCode = 0)
        {
            if (exception != null) eventSource.Message($"[{severity}@{myIpEndPoint}@PID:{pid}] {message}\nException: {exception}");
            else eventSource.Message($"[{severity}@{myIpEndPoint}@PID:{pid}] {message}");
        }
    }

    public class BootstrapProvider : IBootstrapProvider
    {
        public Task Init(string name, IProviderRuntime providerRuntime, IProviderConfiguration config)
        {
            var logger = providerRuntime.GetLogger(nameof(BootstrapProvider));
            this.Name = name;
            
            var grain = providerRuntime.GrainFactory.GetGrain<ICalculatorGrain>(Guid.Empty);
            Task.Factory.StartNew(
                async () =>
                {
                    while (true)
                    {
                        try
                        {
                            var value = await grain.Add(1);
                            logger.Info($"{value - 1} + 1 = {value}");
                            await Task.Delay(TimeSpan.FromSeconds(4));
                        }
                        catch (Exception exception)
                        {
                            logger.Warn(exception.HResult, "Exception in bootstrap provider. Ignoring.", exception);
                        }
                    }
                }).Ignore();
            return Task.FromResult(0);
        }

        public Task Close() => Task.FromResult(0);

        public string Name { get; set; }
    }
}
