using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;

namespace Microsoft.Orleans.ServiceFabric
{
    internal class SiloHostWrapper : ISiloHost
    {
        private SiloHost host;

        public NodeConfiguration NodeConfig => this.host?.NodeConfig;

        public void Start(string siloName, ClusterConfiguration configuration)
        {
            this.host = new SiloHost(siloName, configuration);
            this.host.InitializeOrleansSilo();
            this.host.StartOrleansSilo(catchExceptions: false);
        }

        public void Stop()
        {
            try
            {
                this.host?.StopOrleansSilo();
            }
            catch
            {
                // Ignore.
            }

            this.host?.UnInitializeOrleansSilo();
        }

        public void Dispose()
        {
            this.host?.Dispose();
            this.host = null;
        }
    }
}