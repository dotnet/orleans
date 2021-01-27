using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Hosting;
using System.Net;

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
            // Force TLS 1.2. It should be done by TestUtils.CheckForAzureStorage and TestUtils.CheckForEventHub,
            // but they will not have any effect here since this silo will run on a different AppDomain
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            var deserializedSources = TestClusterHostFactory.DeserializeConfigurationSources(serializedConfigurationSources);
            this.host = TestClusterHostFactory.CreateSiloHost(appDomainName, deserializedSources);
        }

        /// <summary> SiloAddress for this silo. </summary>
        public SiloAddress SiloAddress => this.host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress;

        /// <summary> Gateway address for this silo. </summary>
        public SiloAddress GatewayAddress => this.host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress;

        /// <summary>Starts the silo</summary>
        public void Start() => this.host.StartAsync().GetAwaiter().GetResult();

        /// <summary>Gracefully shuts down the silo</summary>
        public void Shutdown()
        {
            try
            {
                this.host.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                this.host.Dispose();
            }
        }
    }
}
