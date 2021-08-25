using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class DhtGrainLocator : IGrainLocator
    {
        private readonly ILocalGrainDirectory localGrainDirectory;
        private readonly IGrainContext grainContext;
        private readonly ConcurrentQueue<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)> unregistrationQueue = new();
        private int isWorking = 0;

        public DhtGrainLocator(
            ILocalGrainDirectory localGrainDirectory,
            IGrainContext grainContext)
        {
            this.localGrainDirectory = localGrainDirectory;
            this.grainContext = grainContext;
        }

        public async ValueTask<ActivationAddress> Lookup(GrainId grainId) => (await this.localGrainDirectory.LookupAsync(grainId)).Address;

        public async Task<ActivationAddress> Register(ActivationAddress address) => (await this.localGrainDirectory.RegisterAsync(address)).Address;

        public Task Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.unregistrationQueue.Enqueue((tcs, address, cause));
            // Make sure to not run the loop on the Grain Activation context
            this.grainContext.RunOrQueueTask(() => this.UnregisterExecute()).Ignore();
            return tcs.Task;
        }

        public static DhtGrainLocator FromLocalGrainDirectory(LocalGrainDirectory localGrainDirectory)
            => new(localGrainDirectory, localGrainDirectory.RemoteGrainDirectory);

        private async Task UnregisterExecute()
        {
            while (!this.unregistrationQueue.IsEmpty)
            {
                if (Interlocked.CompareExchange(ref this.isWorking, 1, 0) == 1)
                {
                    // Someone is already working
                    return;
                }

                var operations = new List<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)>();
                UnregistrationCause? cause = default;

                try
                {
                    while (this.unregistrationQueue.TryPeek(out var op))
                    {
                        if (cause == default)
                        {
                            cause = op.cause;
                        }
                        else if (cause != op.cause)
                        {
                            break;
                        }
                        this.unregistrationQueue.TryDequeue(out op);
                        operations.Add(op);
                    }

                    if (operations.Any())
                    {
                        await this.localGrainDirectory.UnregisterManyAsync(operations.Select(op => op.address).ToList(), cause.Value);
                        foreach (var op in operations)
                        {
                            op.tcs.SetResult(null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (var op in operations)
                    {
                        op.tcs.SetException(ex);
                    }
                }
                finally
                {
                    // Now we are not working anymore
                    Interlocked.Exchange(ref this.isWorking, 0);
                }
            }
        }

        public void CachePlacementDecision(ActivationAddress address) => this.localGrainDirectory.CachePlacementDecision(address);
        public void InvalidateCache(GrainId grainId) => this.localGrainDirectory.InvalidateCacheEntry(grainId);
        public void InvalidateCache(ActivationAddress address) => this.localGrainDirectory.InvalidateCacheEntry(address);
        public bool TryLookupInCache(GrainId grainId, out ActivationAddress address) => localGrainDirectory.TryCachedLookup(grainId, out address);
    }
}
