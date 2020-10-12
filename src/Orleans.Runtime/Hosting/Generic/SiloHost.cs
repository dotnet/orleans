using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    internal class SiloHost : ISiloHost
    {
        private readonly Silo silo;
        private readonly SiloApplicationLifetime applicationLifetime;
        private bool isDisposing;

        public SiloHost(Silo silo, IServiceProvider services)
        {
            this.Services = services;
            this.silo = silo;
            this.Stopped = silo.SiloTerminated;

            // It is fine for this field to be null in the case that the silo is not the host.
            this.applicationLifetime = services.GetService<IHostApplicationLifetime>() as SiloApplicationLifetime;
        }

        /// <inheritdoc />
        public IServiceProvider Services { get; }

        /// <inheritdoc />
        public Task Stopped { get; }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await this.silo.StartAsync(cancellationToken).ConfigureAwait(false);
            this.applicationLifetime?.NotifyStarted();
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                this.applicationLifetime?.StopApplication();
                await silo.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                this.applicationLifetime?.NotifyStopped();
            }
        }

        /// <inheritdoc />
        public void Dispose() => this.DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            if (!isDisposing)
            {
                this.isDisposing = true;
                if (this.Services is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                else if (this.Services is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }
}