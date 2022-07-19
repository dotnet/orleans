using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;

#nullable enable
namespace Orleans.Core
{
    /// <summary>
    /// Provides functionality for operating on grain state.
    /// Implements the <see cref="Orleans.Core.IStorage{TState}" />
    /// </summary>
    /// <typeparam name="TState">The underlying state type.</typeparam>
    /// <seealso cref="Orleans.Core.IStorage{TState}" />
    public sealed class StateStorageBridge<TState> : IStorage<TState>
    {
        private readonly string name;
        private readonly GrainId grainId;
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

        public StateStorageBridge(string name, GrainId grainId, IGrainStorage store, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (grainId.IsDefault) ArgumentNullException.ThrowIfNull(null, nameof(grainId));
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            this.logger = loggerFactory.CreateLogger(store.GetType());
            this.name = name;
            this.grainId = grainId;
            this.store = store;
            this.grainState = new GrainState<TState>(Activator.CreateInstance<TState>());
        }

        /// <inheritdoc />
        public async Task ReadStateAsync()
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                await store.ReadStateAsync(name, grainId, grainState);
                StorageInstruments.OnStorageRead(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageReadError();

                string errMsg = MakeErrorMsg("ReadState", exc);
                this.logger.LogError((int)ErrorCode.StorageProvider_ReadFailed, exc, "{Message}", errMsg);
                if (!(exc is OrleansException))
                {
                    throw new OrleansException(errMsg, exc);
                }
                throw;
            }
        }

        /// <inheritdoc />
        public async Task WriteStateAsync()
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                await store.WriteStateAsync(name, grainId, grainState);
                StorageInstruments.OnStorageWrite(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageWriteError();
                string errMsgToLog = MakeErrorMsg("WriteState", exc);
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
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                // Clear (most likely Delete) state from external storage
                await store.ClearStateAsync(name, grainId, grainState);
                sw.Stop();

                // Reset the in-memory copy of the state
                grainState.State = Activator.CreateInstance<TState>();

                // Update counters
                StorageInstruments.OnStorageDelete(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageDeleteError();

                string errMsg = MakeErrorMsg("ClearState", exc);
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
            string? errorCode = null;
            (store as IRestExceptionDecoder)?.DecodeException(exc, out _, out errorCode, true);

            return $"Error from storage provider {store.GetType().Name}.{name} during {what} for grain Type={grainId.Type} Id={grainId.Key} Error={errorCode}{Environment.NewLine} {LogFormatter.PrintException(exc)}";
        }
    }
}
