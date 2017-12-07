﻿
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;

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

        /// <summary>
        /// This method is called at the end of the process of activating a grain.
        /// It is called before any messages have been dispatched to the grain.
        /// For grains with declared persistent state, this method is called after the State property has been populated.
        /// </summary>
        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            logger = this.ServiceProvider.GetService<ILoggerFactory>().CreateLogger($"{typeof (StorageFaultGrain).FullName}-{IdentityString}-{RuntimeIdentity}");
            readFaults = new Dictionary<GrainReference, Exception>();
            writeFaults = new Dictionary<GrainReference, Exception>();
            clearfaults = new Dictionary<GrainReference, Exception>();
            logger.Info("Activate.");
        }

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain reads state from a storage provider
        /// </summary>
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        public Task AddFaultOnRead(GrainReference grainReference, Exception exception)
        {
            readFaults.Add(grainReference, exception);
            logger.Info($"Added ReadState fault for {grainReference}.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain writes state to a storage provider
        /// </summary>
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        public Task AddFaultOnWrite(GrainReference grainReference, Exception exception)
        {
            writeFaults.Add(grainReference, exception);
            logger.Info($"Added WriteState fault for {grainReference}.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Adds a storage exception to be thrown when the referenced grain clears state in a storage provider
        /// </summary>
        /// <param name="grainReference"></param>
        /// <param name="exception"></param>
        /// <returns></returns>
        public Task AddFaultOnClear(GrainReference grainReference, Exception exception)
        {
            clearfaults.Add(grainReference, exception);
            logger.Info($"Added ClearState fault for {grainReference}.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for reading.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        public Task OnRead(GrainReference grainReference)
        {
            Exception exception;
            if (readFaults.TryGetValue(grainReference, out exception))
            {
                readFaults.Remove(grainReference);
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for writing.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        public Task OnWrite(GrainReference grainReference)
        {
            Exception exception;
            if (writeFaults.TryGetValue(grainReference, out exception))
            {
                writeFaults.Remove(grainReference);
                throw exception;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Throws a storage exception if one has been added for the grain reference for clearing state.
        /// </summary>
        /// <param name="grainReference"></param>
        /// <returns></returns>
        public Task OnClear(GrainReference grainReference)
        {
            Exception exception;
            if (clearfaults.TryGetValue(grainReference, out exception))
            {
                clearfaults.Remove(grainReference);
                throw exception;
            }
            return Task.CompletedTask;
        }
    }
}
