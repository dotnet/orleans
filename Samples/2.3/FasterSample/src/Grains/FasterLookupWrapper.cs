using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using FASTER.core;

namespace Grains
{
    /// <summary>
    /// Convenience wrapper around the Faster lookup for use in grains.
    /// </summary>
    public class FasterLookupWrapper<TKey, TValue, TInput, TOutput, TContext, TFunctions> : IDisposable
    {
        private IDevice logDevice;
        private IDevice objectLogDevice;

        private FasterKV<TKey, TValue, TInput, TOutput, TContext, TFunctions> lookup;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(Environment.ProcessorCount, Environment.ProcessorCount);

        public FasterLookupWrapper(string grainPath)
        {
            // define the base folder to hold the dictionary state of this grain
            var grainPath = Path.Combine(options.BaseDirectory, GetType().Name, this.GetPrimaryKey().ToString("D"));

            // ensure said folder exists
            if (!Directory.Exists(grainPath))
            {
                Directory.CreateDirectory(grainPath);
            }

            // define the paths for the log files
            var logPath = Path.Combine(grainPath, options.HybridLogDeviceFileTitle);
            var objectPath = Path.Combine(grainPath, options.ObjectLogDeviceFileTitle);

            // define the sub-folder to hold checkpoints
            var checkpointPath = Path.Combine(grainPath, options.CheckpointsSubDirectory);

            // ensure said folder exists
            if (!Directory.Exists(checkpointPath))
            {
                Directory.CreateDirectory(checkpointPath);
            }

            // define the underlying log devices using the default file providers
            logDevice = Devices.CreateLogDevice(logPath, true, true);
            objectLogDevice = Devices.CreateLogDevice(objectPath, true, true);

            // create the faster lookup
            lookup = new FasterKV<int, LookupItem, LookupItem, LookupItem, Empty, LookupItemFunctions>(
                hashBuckets, // hash buckets * 64 bytes per bucket = memory spent by hash table key space
                new LookupItemFunctions(),
                new LogSettings()
                {
                    LogDevice = logDevice,
                    ObjectLogDevice = objectLogDevice,

                    // 2^(memorySizeBits) = memory spent by in-memory log portion
                    // e.g. 2^30 = 1GB in-memory log (the overflow is spilt to disk)
                    MemorySizeBits = memorySizeBits
                },
                new CheckpointSettings
                {
                    CheckpointDir = checkpointPath,
                    CheckPointType = checkpointType
                },
                serializerSettings: new SerializerSettings<int, LookupItem>
                {
                    valueSerializer = () => new LookupItemSerializer()
                },
                comparer: LookupItemFasterKeyComparer.Default);
        }

        public void Dispose()
        {
            try
            {
                lookup.Dispose();
            }
            finally
            {
                try
                {
                    logDevice.Close();
                }
                finally
                {
                    objectLogDevice.Close();
                }
            }
        }
    }
}
