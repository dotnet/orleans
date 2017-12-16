using System;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Orleans.Runtime;
using Orleans.Storage;
using Microsoft.Extensions.Logging;

namespace Orleans.Core
{
    public class StateStorageBridge<TState> : IStorage<TState>
        where TState : new()
    {
        private readonly string name;
        private readonly GrainReference grainRef;
        private readonly IStorageProvider store;
        private readonly GrainState<TState> grainState;
        private readonly ILogger logger;
        public TState State
        {
            get { return grainState.State; }
            set { grainState.State = value; }
        }

        public string Etag
        {
            get { return grainState.ETag; }
        }

        public StateStorageBridge(string name, GrainReference grainRef, IStorageProvider store, ILogger logger)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (grainRef == null) throw new ArgumentNullException(nameof(grainRef));
            if (store == null) throw new ArgumentNullException(nameof(store));
            this.logger = logger;
            this.name = name;
            this.grainRef = grainRef;
            this.store = store;
            this.grainState = new GrainState<TState>(new TState());
        }

        /// <summary>
        /// Async method to cause refresh of the current grain state data from backing store.
        /// Any previous contents of the grain state data will be overwritten.
        /// </summary>
        public async Task ReadStateAsync()
        {
            const string what = "ReadState";
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                await store.ReadStateAsync(name, grainRef, grainState);

                StorageStatisticsGroup.OnStorageRead(store, name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageReadError(store, name, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                logger.Error((int)ErrorCode.StorageProvider_ReadFailed, errMsg, exc);
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsg, exc);
                }
                throw;
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
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                await store.WriteStateAsync(name, grainRef, grainState);
                sw.Stop();
                StorageStatisticsGroup.OnStorageWrite(store, name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageWriteError(store, name, grainRef);
                string errMsgToLog = MakeErrorMsg(what, exc);
                logger.Error((int)ErrorCode.StorageProvider_WriteFailed, errMsgToLog, exc);
                // If error is not specialization of OrleansException, wrap it
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsgToLog, exc);
                }
                throw;
            }
        }

        /// <summary>
        /// Async method to cause write of the current grain state data into backing store.
        /// </summary>
        public async Task ClearStateAsync()
        {
            const string what = "ClearState";
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                // Clear (most likely Delete) state from external storage
                await store.ClearStateAsync(name, grainRef, grainState);
                sw.Stop();

                // Reset the in-memory copy of the state
                grainState.State = new TState();

                // Update counters
                StorageStatisticsGroup.OnStorageDelete(store, name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageStatisticsGroup.OnStorageDeleteError(store, name, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                logger.Error((int)ErrorCode.StorageProvider_DeleteFailed, errMsg, exc);
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsg, exc);
                }
                throw;
            }
        }

        private string MakeErrorMsg(string what, Exception exc)
        {
            HttpStatusCode httpStatusCode;
            string errorCode = string.Empty;

            var decoder = store as IRestExceptionDecoder;
            decoder?.DecodeException(exc, out httpStatusCode, out errorCode, true);

            return string.Format("Error from storage provider {0} during {1} for grain Type={2} Pk={3} Id={4} Error={5}" + Environment.NewLine + " {6}",
                $"{this.store.GetType().Name}.{this.store.Name}", what, name, grainRef.GrainId.ToDetailedString(), grainRef, errorCode, LogFormatter.PrintException(exc));
        }
    }
}
