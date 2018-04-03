using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using AzureWorker.Interfaces;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.Diagnostics;
using Microsoft.WindowsAzure.ServiceRuntime;
using Microsoft.WindowsAzure.Storage;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace OrleansClient
{
    public class WorkerRole : RoleEntryPoint
    {
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent runCompleteEvent = new ManualResetEvent(false);
        private IClusterClient orleansClient;

        public override void Run()
        {
            Trace.TraceInformation("OrleansClient is running");

            try
            {
                this.RunAsync(this.cancellationTokenSource.Token).Wait();
            }
            finally
            {
                this.runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            bool result = base.OnStart();

            var dataConnectionString = RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
            var clusterId = RoleEnvironment.DeploymentId;

            var builder = new ClientBuilder()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = clusterId;
                    options.ServiceId = "AzureWorkerRoleSample";
                })
                .UseAzureStorageClustering(config => config.ConnectionString = dataConnectionString);

            orleansClient = builder.Build();

            orleansClient.Connect(async ex =>
            {
                Trace.TraceInformation(ex.Message);

                await Task.Delay(TimeSpan.FromSeconds(3));
                return true;
            }).Wait();

            return result;
        }

        public override void OnStop()
        {
            Trace.TraceInformation("OrleansClient is stopping");

            this.cancellationTokenSource.Cancel();
            this.runCompleteEvent.WaitOne();

            base.OnStop();

            Trace.TraceInformation("OrleansClient has stopped");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var aliceGrain = orleansClient.GetGrain<IGreetGrain>("Alice");
            while (true)
            {
                string greet = await aliceGrain.Greet("Bob");

                Trace.TraceInformation(greet);

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }
}
