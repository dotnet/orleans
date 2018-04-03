using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.Hosting
{
    internal class SiloWrapper : ISiloHost
    {
        private readonly Silo silo;
        private bool isDisposing;

        public SiloWrapper(Silo silo, IServiceProvider services)
        {
            this.Services = services;
            this.silo = silo;
            this.Stopped = silo.SiloTerminated;
        }

        /// <inheritdoc />
        public IServiceProvider Services { get; }

        /// <inheritdoc />
        public Task Stopped { get; }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return this.silo.StartAsync(cancellationToken);
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await silo.StopAsync(cancellationToken);
            await this.Stopped;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (!isDisposing)
            {
                this.isDisposing = true;
                (this.Services as IDisposable)?.Dispose();
            }
        }
    }
}