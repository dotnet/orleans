using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;
using Grains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;

namespace Grains
{
    [Reentrant]
    public class FasterGrain : Grain, IFasterGrain
    {
        private readonly ILogger<FasterGrain> logger;
        private readonly FasterOptions options;
        private bool started = false;
        private FasterLookupWrapper<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions> lookup;

        public FasterGrain(ILogger<FasterGrain> logger, IOptions<FasterOptions> options)
        {
            this.logger = logger;
            this.options = options.Value;
        }

        /// <summary>
        /// This sets up a faster lookup as per the given parameters.
        /// This code is only here to facilitate benchmarking.
        /// In a production design, the code below would sit in OnActivateAsync() with parameters taken from injected options.
        /// </summary>
        /// <param name="hashBuckets">The number of hash buckets in the key space.</param>
        /// <param name="memorySizeBits">The power of two size for the in-memory log portion size.</param>
        /// <param name="checkpointType">Whether to take a full snapshot of state or just fold over the log.</param>
        /// <returns></returns>
        public Task ConfigureAsync(int hashBuckets, int memorySizeBits, CheckpointType checkpointType)
        {
            if (lookup != null) throw new ArgumentException();

            lookup = new FasterLookupWrapper()

            // disallow starting again
            started = true;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Terminates the faster lookup.
        /// This method is here to facilitate releasing memory during benchmarking.
        /// Allows the grain to be configured again.
        /// </summary>
        /// <returns></returns>
        public Task ReleaseAsync()
        {
            lookup?.Dispose();
            lookup = null;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets a single item in the lookup.
        /// This is a blind update.
        /// </summary>
        /// <param name="item">The item to set.</param>
        /// <returns></returns>
        public Task SetAsync(LookupItem item)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();

                    var key = item.Key;
                    lookup.Upsert(ref key, ref item, Empty.Default, 0);
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
            });
        }

        /// <summary>
        /// Sets a range of item in the lookup.
        /// This is a blind update.
        /// </summary>
        /// <param name="items">The items to set.</param>
        /// <returns></returns>
        public Task SetRangeAsync(ImmutableList<LookupItem> items)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    for (var i = 0; i < items.Count; ++i)
                    {
                        var item = items[i];
                        var key = item.Key;
                        lookup.Upsert(ref key, ref item, Empty.Default, i);
                    }
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                return Task.CompletedTask;
            });
        }

        public Task SnapshotAsync()
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    lookup.CompletePending(true);
                    lookup.TakeFullCheckpoint(out var token);
                    lookup.CompleteCheckpoint(true);
                    lookup.StopSession();
                }
                finally
                {
                    semaphore.Release();
                }
                return Task.CompletedTask;
            });
        }

        public Task<LookupItem> TryGetAsync(int key)
        {
            var session = Guid.Empty;
            try
            {
                session = lookup.StartSession();
                LookupItem result = null;
                if (lookup.Read(ref key, ref result, ref result, Empty.Default, 0) == Status.ERROR)
                {
                    throw new ApplicationException();
                }
                return Task.FromResult(result);
            }
            finally
            {
                if (session != Guid.Empty)
                {
                    lookup.StopSession();
                }
            }
        }

        public Task SetRangeDeltaAsync(ImmutableList<LookupItem> items)
        {
            return Task.Run(async () =>
            {
                await semaphore.WaitAsync();

                try
                {
                    lookup.StartSession();
                    for (var i = 0; i < items.Count; ++i)
                    {
                        var item = items[i];
                        var key = item.Key;
                        lookup.RMW(ref key, ref item, Empty.Default, i);
                    }
                }
                finally
                {
                    try
                    {
                        lookup.StopSession();
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }
                return Task.CompletedTask;
            });
        }
    }
}