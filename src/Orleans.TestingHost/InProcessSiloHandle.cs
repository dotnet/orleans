using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Represents a handle to a silo that is deployed in the same process and AppDomain.
    /// </summary>
    public class InProcessSiloHandle : SiloHandle
    {
        private bool isActive = true;
        
        /// <summary>Gets a reference to the silo host.</summary>
        public ISiloHost SiloHost { get; private set; }

        /// <inheritdoc />
        public override bool IsActive => isActive;

        /// <summary>Creates a new silo and returns a handle to it.</summary>
        /// <param name="siloName">The name for the new silo.</param>
        /// <param name="configurationSources">
        /// The configuration sources, interpreted by <see cref="TestClusterHostFactory.CreateSiloHost"/>.
        /// </param>
        public static SiloHandle Create(
            string siloName,
            IList<IConfigurationSource> configurationSources)
        {
            var host = TestClusterHostFactory.CreateSiloHost(siloName, configurationSources);
            host.StartAsync().GetAwaiter().GetResult();

            var retValue = new InProcessSiloHandle
            {
                Name = siloName,
                SiloHost = host,
                SiloAddress = host.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress,
                GatewayAddress = host.Services.GetRequiredService<ILocalSiloDetails>().GatewayAddress,
            };

            return retValue;
        }

        /// <inheritdoc />
        public override void StopSilo(bool stopGracefully)
        {
            if (!IsActive) return;

            var cancellation = new CancellationTokenSource();
            if (!stopGracefully) cancellation.Cancel();

            try
            {
                this.SiloHost.StopAsync(cancellation.Token).GetAwaiter().GetResult();
            }
            catch (Exception exc)
            {
                WriteLog(exc);
                throw;
            }
            finally
            {
                this.isActive = false;
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (!this.IsActive) return;

            if (disposing)
            {
                try
                {
                    StopSilo(true);
                }
                catch
                {
                }

                this.SiloHost?.Dispose();
            }
        }

        private void WriteLog(object value)
        {
            Console.WriteLine(value?.ToString());
        }
    }
}