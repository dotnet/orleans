using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        public IHost SiloHost { get; private set; }

        /// <inheritdoc />
        public override bool IsActive => isActive;

        public static async Task<SiloHandle> CreateAsync(
            string siloName,
            IConfiguration configuration)
        {
            var host = await Task.Run(async () =>
            {
                var result = TestClusterHostFactory.CreateSiloHost(siloName, configuration);
                await result.StartAsync();
                return result;
            });

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
        public override async Task StopSiloAsync(bool stopGracefully)
        {
            var cancellation = new CancellationTokenSource();
            var ct = cancellation.Token;

            if (!stopGracefully)
                cancellation.Cancel();

            await StopSiloAsync(ct);
        }

        public override async Task StopSiloAsync(CancellationToken ct)
        {
            if (!IsActive) return;

            try
            {
                await Task.Run(() => this.SiloHost.StopAsync(ct));
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
                    StopSiloAsync(true).GetAwaiter().GetResult();
                }
                catch
                {
                }

                this.SiloHost?.Dispose();
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (!this.IsActive) return;

            try
            {
                await StopSiloAsync(true).ConfigureAwait(false);
            }
            finally
            {
                if (this.SiloHost is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        private void WriteLog(object value)
        {
            Console.WriteLine(value?.ToString());
        }
    }
}