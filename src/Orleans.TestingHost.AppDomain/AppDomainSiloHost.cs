using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Allows programmatically hosting an Orleans silo in the current app domain, exposing some marshallable members via remoting.
    /// </summary>
    public class AppDomainSiloHost : MarshalByRefObject
    {
        private readonly ISiloHost host;

        /// <summary>Creates and initializes a silo in the current app domain.</summary>
        /// <param name="appDomainName">Name of this silo.</param>
        /// <param name="serializedConfigurationSources">Silo config data to be used for this silo.</param>
        public AppDomainSiloHost(string appDomainName, string serializedConfigurationSources)
        {
            var deserializedSources = TestClusterHostFactory.DeserializeConfigurationSources(serializedConfigurationSources);
            this.host = TestClusterHostFactory.CreateSiloHost(appDomainName, deserializedSources);
            this.AppDomainTestHook = new AppDomainTestHooks(this.host);
        }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        /// <summary> Gateway address for this silo. </summary>
        public SiloAddress GatewayAddress => this.host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress;

        internal AppDomainTestHooks AppDomainTestHook { get; }
        
        /// <summary>Starts the silo</summary>
        public void Start()
        {
            this.host.StartAsync().GetAwaiter().GetResult();
        }

        /// <summary>Gracefully shuts down the silo</summary>
        public void Shutdown()
        {
            this.host.StopAsync().GetAwaiter().GetResult();
        }
    }
}
