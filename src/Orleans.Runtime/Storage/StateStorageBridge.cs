using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Core
{

    /// <summary>
    /// Provides functionality for operating on grain state.
    /// Implements the <see cref="Orleans.Core.IStorage{TState}" />
    /// </summary>
    /// <typeparam name="TState">The underlying state type.</typeparam>
    /// <seealso cref="Orleans.Core.IStorage{TState}" />
    public class StateStorageBridge<TState> : IStorage<TState>
    {
        private readonly string name;
        private readonly GrainReference grainRef;
        private readonly IGrainStorage store;
        private readonly GrainState<TState> grainState;
        private readonly ILogger logger;

        /// <inheritdoc/>
        public TState State
        {
            get
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                return grainState.State;
            }

            set
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                grainState.State = value;
            }
        }

        /// <inheritdoc/>
        public string Etag => grainState.ETag;

        /// <inheritdoc/>
        public bool RecordExists => grainState.RecordExists;

        public StateStorageBridge(string name, GrainReference grainRef, IGrainStorage store, ILoggerFactory loggerFactory)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (grainRef == null) throw new ArgumentNullException(nameof(grainRef));
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));

            this.logger = loggerFactory.CreateLogger(store.GetType());
            this.name = name;
            this.grainRef = grainRef;
            this.store = store;
            this.grainState = new GrainState<TState>(Activator.CreateInstance<TState>());
        }

        /// <inheritdoc />
        public async Task ReadStateAsync()
        {
            const string what = "ReadState";
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                await store.ReadStateAsync(name, grainRef, grainState);

                StorageInstruments.OnStorageRead(name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageReadError(name, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                this.logger.LogError((int)ErrorCode.StorageProvider_ReadFailed, exc, "{Message}", errMsg);
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

        /// <inheritdoc />
        public async Task WriteStateAsync()
        {
            const string what = "WriteState";
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                Stopwatch sw = Stopwatch.StartNew();
                await store.WriteStateAsync(name, grainRef, grainState);
                sw.Stop();
                StorageInstruments.OnStorageWrite(name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageWriteError(name, grainRef);
                string errMsgToLog = MakeErrorMsg(what, exc);
                this.logger.LogError((int)ErrorCode.StorageProvider_WriteFailed, exc, "{Message}", errMsgToLog);
                // If error is not specialization of OrleansException, wrap it
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsgToLog, exc);
                }
                throw;
            }
        }

        /// <inheritdoc />
        public async Task ClearStateAsync()
        {
            const string what = "ClearState";
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                Stopwatch sw = Stopwatch.StartNew();
                // Clear (most likely Delete) state from external storage
                await store.ClearStateAsync(name, grainRef, grainState);
                sw.Stop();

                // Reset the in-memory copy of the state
                grainState.State = Activator.CreateInstance<TState>();

                // Update counters
                StorageInstruments.OnStorageDelete(name, grainRef, sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageDeleteError(name, grainRef);

                string errMsg = MakeErrorMsg(what, exc);
                this.logger.LogError((int)ErrorCode.StorageProvider_DeleteFailed, exc, "{Message}", errMsg);
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsg, exc);
                }
                throw;
            }
        }

        private string MakeErrorMsg(string what, Exception exc)
        {
            string errorCode = null;
            (store as IRestExceptionDecoder)?.DecodeException(exc, out _, out errorCode, true);

            return @$"Error from storage provider {$"{store.GetType().Name}.{name}"} during {what} for grain Type={name} Pk={grainRef.GrainId} Id={grainRef} Error={errorCode
                }{Environment.NewLine} {LogFormatter.PrintException(exc)}";
        }
    }
}
