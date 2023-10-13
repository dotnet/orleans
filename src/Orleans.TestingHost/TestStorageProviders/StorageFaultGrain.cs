
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;

namespace Orleans.TestingHost
{
    /// <summary>
    /// Grain that tracks storage exceptions to be injected.
    /// </summary>
    public class StorageFaultGrain : Grain, IStorageFaultGrain
    {
        private ILogger logger;
        private Dictionary<GrainId, Exception> readFaults;
        private Dictionary<GrainId, Exception> writeFaults;
        private Dictionary<GrainId, Exception> clearfaults;

        /// <inheritdoc />
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
            logger = this.ServiceProvider.GetService<ILoggerFactory>().CreateLogger($"{typeof (StorageFaultGrain).FullName}-{IdentityString}-{RuntimeIdentity}");
            readFaults = new();
            writeFaults = new();
            clearfaults = new();
            logger.LogInformation("Activate.");
        }

        /// <inheritdoc />
        public Task AddFaultOnRead(GrainId grainId, Exception exception)
        {
            readFaults.Add(grainId, exception);
            logger.LogInformation("Added ReadState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddFaultOnWrite(GrainId grainId, Exception exception)
        {
            writeFaults.Add(grainId, exception);
            logger.LogInformation("Added WriteState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddFaultOnClear(GrainId grainId, Exception exception)
        {
            clearfaults.Add(grainId, exception);
            logger.LogInformation("Added ClearState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnRead(GrainId grainId)
        {
            if (readFaults.Remove(grainId, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnWrite(GrainId grainId)
        {
            if (writeFaults.Remove(grainId, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnClear(GrainId grainId)
        {
            if (clearfaults.Remove(grainId, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }
    }
}
