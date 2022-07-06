
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
        private Dictionary<GrainReference, Exception> readFaults;
        private Dictionary<GrainReference, Exception> writeFaults;
        private Dictionary<GrainReference, Exception> clearfaults;

        /// <inheritdoc />
        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
            logger = this.ServiceProvider.GetService<ILoggerFactory>().CreateLogger($"{typeof (StorageFaultGrain).FullName}-{IdentityString}-{RuntimeIdentity}");
            readFaults = new Dictionary<GrainReference, Exception>();
            writeFaults = new Dictionary<GrainReference, Exception>();
            clearfaults = new Dictionary<GrainReference, Exception>();
            logger.LogInformation("Activate.");
        }

        /// <inheritdoc />
        public Task AddFaultOnRead(GrainReference grainReference, Exception exception)
        {
            readFaults.Add(grainReference, exception);
            logger.LogInformation("Added ReadState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddFaultOnWrite(GrainReference grainReference, Exception exception)
        {
            writeFaults.Add(grainReference, exception);
            logger.LogInformation("Added WriteState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task AddFaultOnClear(GrainReference grainReference, Exception exception)
        {
            clearfaults.Add(grainReference, exception);
            logger.LogInformation("Added ClearState fault for {GrainId}.", GrainId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnRead(GrainReference grainReference)
        {
            if (readFaults.Remove(grainReference, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnWrite(GrainReference grainReference)
        {
            if (writeFaults.Remove(grainReference, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task OnClear(GrainReference grainReference)
        {
            if (clearfaults.Remove(grainReference, out var exception))
            {
                throw exception;
            }
            return Task.CompletedTask;
        }
    }
}
