using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Channels;
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
        private readonly ILocalGrainDirectory _localGrainDirectory;
        private readonly IGrainContext _grainContext;
        private readonly object _initLock = new();
        private BatchedDeregistrationWorker _forceWorker;
        private BatchedDeregistrationWorker _neaWorker;

        public DhtGrainLocator(
            ILocalGrainDirectory localGrainDirectory,
            IGrainContext grainContext)
        {
            _localGrainDirectory = localGrainDirectory;
            _grainContext = grainContext;
        }

        public async ValueTask<GrainAddress> Lookup(GrainId grainId) => (await _localGrainDirectory.LookupAsync(grainId)).Address;

        public async Task<GrainAddress> Register(GrainAddress address) => (await _localGrainDirectory.RegisterAsync(address)).Address;

        public Task Unregister(GrainAddress address, UnregistrationCause cause)
        {
            EnsureInitialized();

            // If this ever gets more complicated, we can use a list or internally manage things within a single worker.
            var worker = cause switch
            {
                UnregistrationCause.Force => _forceWorker,
                UnregistrationCause.NonexistentActivation => _neaWorker,
                _ => throw new ArgumentOutOfRangeException($"Unregistration cause {cause} is unknown and is not supported. This is a bug."),
            };

            return worker.Unregister(address);
            
            void EnsureInitialized()
            {
                // Unfortunately, for now we need to perform this initialization lazily, since a SystemTarget does not become valid
                // until it's registered with the Catalog (see Catalog.RegisterSystemTarget), which can happen after this instance
                // is constructed.
                if (_forceWorker is not null && _neaWorker is not null)
                {
                    return;
                }

                lock (_initLock)
                {
                    if (_forceWorker is not null && _neaWorker is not null)
                    {
                        return;
                    }

                    _forceWorker = new BatchedDeregistrationWorker(_localGrainDirectory, _grainContext, UnregistrationCause.Force);
                    _neaWorker = new BatchedDeregistrationWorker(_localGrainDirectory, _grainContext, UnregistrationCause.NonexistentActivation);
                }
            }
        }

        public static DhtGrainLocator FromLocalGrainDirectory(LocalGrainDirectory localGrainDirectory)
            => new(localGrainDirectory, localGrainDirectory.RemoteGrainDirectory);

        public void CachePlacementDecision(GrainAddress address) => _localGrainDirectory.CachePlacementDecision(address);
        public void InvalidateCache(GrainId grainId) => _localGrainDirectory.InvalidateCacheEntry(grainId);
        public void InvalidateCache(GrainAddress address) => _localGrainDirectory.InvalidateCacheEntry(address);
        public bool TryLookupInCache(GrainId grainId, out GrainAddress address) => _localGrainDirectory.TryCachedLookup(grainId, out address);

        private class BatchedDeregistrationWorker
        {
            private const int OperationBatchSizeLimit = 2_000;
            private readonly ILocalGrainDirectory _localGrainDirectory;
            private readonly IGrainContext _grainContext;
            private readonly UnregistrationCause _cause;
            private readonly Channel<(TaskCompletionSource<bool> tcs, GrainAddress address)> _queue;
#pragma warning disable IDE0052 // Remove unread private members
            private readonly Task _runTask;
#pragma warning restore IDE0052 // Remove unread private members

            public BatchedDeregistrationWorker(
                ILocalGrainDirectory localGrainDirectory,
                IGrainContext grainContext,
                UnregistrationCause cause)
            {
                _localGrainDirectory = localGrainDirectory;
                _grainContext = grainContext;
                _cause = cause;
                _queue = Channel.CreateUnbounded<(TaskCompletionSource<bool> tcs, GrainAddress address)>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
                _runTask = _grainContext.RunOrQueueTask(() => ProcessDeregistrationQueue());
            }

            public Task Unregister(GrainAddress address)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _queue.Writer.TryWrite((tcs, address));
                return tcs.Task;
            }

            private async Task ProcessDeregistrationQueue()
            {
                var operations = new List<TaskCompletionSource<bool>>();
                var addresses = new List<GrainAddress>();
                var reader = _queue.Reader;

                while (await reader.WaitToReadAsync())
                {
                    // Process a batch of work.
                    try
                    {
                        operations.Clear();
                        addresses.Clear();

                        while (operations.Count < OperationBatchSizeLimit && reader.TryRead(out var op))
                        {
                            operations.Add(op.tcs);
                            addresses.Add(op.address);
                        }

                        if (operations.Count > 0)
                        {
                            await _localGrainDirectory.UnregisterManyAsync(addresses, _cause);
                            foreach (var op in operations)
                            {
                                op.TrySetResult(true);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var op in operations)
                        {
                            op.TrySetException(ex);
                        }
                    }
                }
            }
        }
    }
}
