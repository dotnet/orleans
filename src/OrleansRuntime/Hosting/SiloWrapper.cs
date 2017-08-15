using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Hosting
{
    internal class SiloWrapper : ISilo
    {
        private readonly Silo silo;

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
            if (!cancellationToken.IsCancellationRequested)
            {
                this.silo.Start();
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                this.silo.Stop();
            }
            else
            {
                this.silo.Shutdown();
            }

            return this.Stopped;
        }
    }
}