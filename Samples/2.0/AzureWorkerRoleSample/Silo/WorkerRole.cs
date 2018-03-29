using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.ServiceRuntime;
using Orleans.Configuration;
using Orleans.Hosting;

namespace Silo
{
    public class WorkerRole : RoleEntryPoint
    {
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly ManualResetEvent runCompleteEvent = new ManualResetEvent(false);
        private ISiloHost siloHost;

        public override void Run()
        {
            Trace.TraceInformation("Silo is running");

            try
            {
                this.RunAsync(this.cancellationTokenSource.Token).Wait();
                
                runCompleteEvent.WaitOne();
            }
            finally
            {
                this.runCompleteEvent.Set();
            }
        }

        public override bool OnStart()
        {
            bool result = base.OnStart();

            Trace.TraceInformation("Silo has been started");

            return result;
        }

        public override void OnStop()
        {
            Trace.TraceInformation("Silo is stopping");

            this.cancellationTokenSource.Cancel();
            this.runCompleteEvent.WaitOne();

            base.OnStop();

            Trace.TraceInformation("Silo has stopped");
        }

        private Task RunAsync(CancellationToken cancellationToken)
        {
            var proxyPort = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansProxyEndpoint"].IPEndpoint.Port;
            var siloEndpoint = RoleEnvironment.CurrentRoleInstance.InstanceEndpoints["OrleansSiloEndpoint"].IPEndpoint;
            var connectionString = RoleEnvironment.GetConfigurationSettingValue("DataConnectionString");
            var deploymentId = RoleEnvironment.DeploymentId;

            var builder = new SiloHostBuilder()
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = deploymentId;
                    options.ServiceId = "AzureWorkerRoleSample";
                })
                .ConfigureEndpoints(siloEndpoint.Address, siloEndpoint.Port, proxyPort)
                .UseAzureStorageClustering(options => options.ConnectionString = connectionString);

            siloHost = builder.Build();

            return siloHost.StartAsync(cancellationToken);
        }
    }
}
