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
        private readonly OrleansTaskScheduler taskScheduler;
        private readonly IGrainContext grainContext;
        private readonly ConcurrentQueue<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)> unregistrationQueue = new ConcurrentQueue<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)>();
        private int isWorking = 0;

        public DhtGrainLocator(
            ILocalGrainDirectory localGrainDirectory,
            OrleansTaskScheduler taskScheduler,
            IGrainContext grainContext)
        {
            this.localGrainDirectory = localGrainDirectory;
            this.taskScheduler = taskScheduler;
            this.grainContext = grainContext;
        }

        public async ValueTask<ActivationAddress> Lookup(GrainId grainId) => (await this.localGrainDirectory.LookupAsync(grainId)).Address;

        public bool TryLocalLookup(GrainId grainId, out ActivationAddress address)
        {
            if (this.localGrainDirectory.LocalLookup(grainId, out var addressAndTag))
            {
                address = addressAndTag.Address;
                return true;
            }

            address = null;
            return false;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address) => (await this.localGrainDirectory.RegisterAsync(address)).Address;

        public Task Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.unregistrationQueue.Enqueue((tcs, address, cause));
            // Make sure to not run the loop on the Grain Activation context
            this.taskScheduler.RunOrQueueTask(() => this.UnregisterExecute(), this.grainContext).Ignore();
            return tcs.Task;
        }

        public static DhtGrainLocator FromLocalGrainDirectory(LocalGrainDirectory localGrainDirectory)
            => new(localGrainDirectory, localGrainDirectory.Scheduler, localGrainDirectory.RemoteGrainDirectory);

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
    }
}
