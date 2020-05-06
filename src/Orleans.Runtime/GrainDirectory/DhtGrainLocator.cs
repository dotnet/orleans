using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.GrainDirectory;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// Implementation of <see cref="IGrainLocator"/> that uses the in memory distributed directory of Orleans
    /// </summary>
    internal class DhtGrainLocator : IGrainLocator
    {
        private readonly ILocalGrainDirectory localGrainDirectory;
        private readonly ConcurrentQueue<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)> unregistrationQueue = new ConcurrentQueue<(TaskCompletionSource<object> tcs, ActivationAddress address, UnregistrationCause cause)>();
        private int isWorking = 0;

        public DhtGrainLocator(ILocalGrainDirectory localGrainDirectory)
        {
            this.localGrainDirectory = localGrainDirectory;
        }

        public async Task<List<ActivationAddress>> Lookup(GrainId grainId)
            => (await this.localGrainDirectory.LookupAsync(grainId)).Addresses;

        public bool TryLocalLookup(GrainId grainId, out List<ActivationAddress> addresses)
        {
            if (this.localGrainDirectory.LocalLookup(grainId, out var addressesAndTag))
            {
                addresses = addressesAndTag.Addresses;
                return true;
            }
            addresses = null;
            return false;
        }

        public async Task<ActivationAddress> Register(ActivationAddress address)
            => (await this.localGrainDirectory.RegisterAsync(address, singleActivation: true)).Address;

        public  Task Unregister(ActivationAddress address, UnregistrationCause cause)
        {
            var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.unregistrationQueue.Enqueue((tcs, address, cause));
            UnregisterExecute().Ignore();
            return tcs.Task;
        }

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
