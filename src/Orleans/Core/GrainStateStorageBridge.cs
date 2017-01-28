// ﻿#define REREAD_STATE_AFTER_WRITE_FAILED

using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Core
{
    internal class GrainStateStorageBridge : IStorage
    {
        private readonly IStorageProvider store;
        private IStatefulGrain statefulGrain;
        private Grain baseGrain;
        private readonly string grainTypeName;

        public GrainStateStorageBridge(string grainTypeName, IStorageProvider store)
        {
            if (grainTypeName == null)
            {
                throw new ArgumentNullException("grainTypeName", "No grain type name supplied");
            }
            if (store == null)
            {
                throw new ArgumentNullException("store", "No storage provider supplied");
            }
           
            this.grainTypeName = grainTypeName;
            this.store = store;
        }

        internal void SetGrain(Grain grain)
        {
            if(grain == null)
                throw new ArgumentNullException(nameof(grain));

            statefulGrain = grain as IStatefulGrain;

            if(statefulGrain == null)
                throw new ArgumentException("Attempt to configure storage bridge for a non-perisstent grain.", nameof(grain));

            if (statefulGrain.GrainState == null)
                throw new ArgumentException("No grain state object supplied", nameof(grain));
            
            this.baseGrain = grain;
        }

        /// <summary>
        /// Async method to cause refresh of the current grain state data from backing store.
        /// Any previous contents of the grain state data will be overwritten.
        /// </summary>
        public async Task ReadStateAsync()
        {
            const string what = "ReadState";
            Stopwatch sw = Stopwatch.StartNew();
            GrainReference grainRef = baseGrain.GrainReference;
            try
            {
                await store.ReadStateAsync(grainTypeName, grainRef, statefulGrain.GrainState);

                StorageStatisticsGroup.OnStorageRead(store, grainTypeName, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageReadError(store, grainTypeName, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                store.Log.Error((int) ErrorCode.StorageProvider_ReadFailed, errMsg, exc);
                throw new OrleansException(errMsg, exc);
            }
            finally
            {
                sw.Stop();
            }
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        public async Task WriteStateAsync()
        {
            const string what = "WriteState";
            Stopwatch sw = Stopwatch.StartNew();
            GrainReference grainRef = baseGrain.GrainReference;
            Exception errorOccurred;
            try
            {
                await store.WriteStateAsync(grainTypeName, grainRef, statefulGrain.GrainState);
                StorageStatisticsGroup.OnStorageWrite(store, grainTypeName, grainRef, sw.Elapsed);
                errorOccurred = null;
            }
            catch (Exception exc)
            {
                errorOccurred = exc;
            }
            // Note, we can't do this inside catch block above, because await is not permitted there.
            if (errorOccurred != null)
            {
                StorageStatisticsGroup.OnStorageWriteError(store, grainTypeName, grainRef);

                string errMsgToLog = MakeErrorMsg(what, errorOccurred);
                store.Log.Error((int) ErrorCode.StorageProvider_WriteFailed, errMsgToLog, errorOccurred);
                // If error is not specialization of OrleansException, wrap it
                if (!(errorOccurred is OrleansException))
                {
                    errorOccurred = new OrleansException(errMsgToLog, errorOccurred);
                }

#if REREAD_STATE_AFTER_WRITE_FAILED
                // Force rollback to previously stored state
                try
                {
                    sw.Restart();
                    store.Log.Warn(ErrorCode.StorageProvider_ForceReRead, "Forcing re-read of last good state for grain Type={0}", grainTypeName);
                    await store.ReadStateAsync(grainTypeName, grainRef, grain.GrainState);
                    StorageStatisticsGroup.OnStorageRead(store, grainTypeName, grainRef, sw.Elapsed);
                }
                catch (Exception exc)
                {
                    StorageStatisticsGroup.OnStorageReadError(store, grainTypeName, grainRef);

                    // Should we ignore this secondary error, and just return the original one?
                    errMsgToLog = MakeErrorMsg("re-read state from store after write error", exc);
                    errorOccurred = new OrleansException(errMsgToLog, exc);
                }
#endif
            }
            sw.Stop();
            if (errorOccurred != null)
            {
                throw errorOccurred;
            }
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        public async Task ClearStateAsync()
        {
            const string what = "ClearState";
            Stopwatch sw = Stopwatch.StartNew();
            GrainReference grainRef = baseGrain.GrainReference;
            try
            {
                // Clear (most likely Delete) state from external storage
                await store.ClearStateAsync(grainTypeName, grainRef, statefulGrain.GrainState);

                // Reset the in-memory copy of the state
                statefulGrain.GrainState.State = Activator.CreateInstance(statefulGrain.GrainState.State.GetType());

                // Update counters
                StorageStatisticsGroup.OnStorageDelete(store, grainTypeName, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageDeleteError(store, grainTypeName, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                store.Log.Error((int) ErrorCode.StorageProvider_DeleteFailed, errMsg, exc);
                throw new OrleansException(errMsg, exc);
            }
            finally
            {
                sw.Stop();
            }
        }

        private string MakeErrorMsg(string what, Exception exc)
        {
            var httpStatusCode = HttpStatusCode.Unused;
            string errorCode = String.Empty;

            var decoder = store as IRestExceptionDecoder;
            if(decoder != null)
                decoder.DecodeException(exc, out httpStatusCode, out errorCode, true);

            GrainReference grainReference = baseGrain.GrainReference;
            return string.Format("Error from storage provider during {0} for grain Type={1} Pk={2} Id={3} Error={4}" + Environment.NewLine + " {5}",
                what, grainTypeName, grainReference.GrainId.ToDetailedString(), grainReference, errorCode, LogFormatter.PrintException(exc));
        }
    }
}
