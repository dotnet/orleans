#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Core
{
    /// <summary>
    /// Provides functionality for operating on grain state.
    /// Implements the <see cref="IStorage{TState}" />
    /// </summary>
    /// <typeparam name="TState">The underlying state type.</typeparam>
    /// <seealso cref="IStorage{TState}" />
    public class StateStorageBridge<TState> : IStorage<TState>, IGrainMigrationParticipant
    {
        private readonly string _name;
        private readonly IGrainContext _grainContext;
        private readonly IGrainStorage _store;
        private readonly ILogger _logger;
        private GrainState<TState>? _grainState;

        /// <inheritdoc/>
        public TState State
        {
            get
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                return GrainState.State;
            }

            set
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                GrainState.State = value;
            }
        }

        private GrainState<TState> GrainState => _grainState ??= new GrainState<TState>(Activator.CreateInstance<TState>());
        internal bool IsStateInitialized => _grainState != null;

        /// <inheritdoc/>
        public string? Etag { get => GrainState.ETag; set => GrainState.ETag = value; }

        /// <inheritdoc/>
        public bool RecordExists => GrainState.RecordExists;

        public StateStorageBridge(string name, IGrainContext grainContext, IGrainStorage store, ILoggerFactory loggerFactory)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(grainContext);
            ArgumentNullException.ThrowIfNull(store);
            ArgumentNullException.ThrowIfNull(loggerFactory);

            _logger = loggerFactory.CreateLogger(store.GetType());
            _name = name;
            _grainContext = grainContext;
            _store = store;
        }

        /// <inheritdoc />
        public async Task ReadStateAsync()
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                await _store.ReadStateAsync(_name, _grainContext.GrainId, GrainState);
                StorageInstruments.OnStorageRead(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageReadError();
                OnError(exc, ErrorCode.StorageProvider_ReadFailed, nameof(ReadStateAsync));
            }
        }

        /// <inheritdoc />
        public async Task WriteStateAsync()
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                await _store.WriteStateAsync(_name, _grainContext.GrainId, GrainState);
                StorageInstruments.OnStorageWrite(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageWriteError();
                OnError(exc, ErrorCode.StorageProvider_WriteFailed, nameof(WriteStateAsync));
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
                await _store.ClearStateAsync(_name, _grainContext.GrainId, GrainState);
                sw.Stop();

                // Reset the in-memory copy of the state
                GrainState.State = Activator.CreateInstance<TState>();

                // Update counters
                StorageInstruments.OnStorageDelete(sw.Elapsed);
            }
            catch (Exception exc)
            {
                StorageInstruments.OnStorageDeleteError();
                OnError(exc, ErrorCode.StorageProvider_DeleteFailed, nameof(ClearStateAsync));
            }
        }

        public void OnDehydrate(IDehydrationContext dehydrationContext)
        {
            try
            {
                dehydrationContext.TryAddValue($"state.{_name}", _grainState);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to dehydrate state named {StateName} for grain {GrainId}", _name, _grainContext.GrainId);

                // We must throw here since we do not know that the dehydration context is in a clean state after this.
                throw;
            }
        }

        public void OnRehydrate(IRehydrationContext rehydrationContext)
        {
            try
            {
                rehydrationContext.TryGetValue($"state.{_name}", out _grainState);
            }
            catch (Exception exception)
            {
                // It is ok to swallow this exception, since state rehydration is best-effort.
                _logger.LogError(exception, "Failed to rehydrate state named {StateName} for grain {GrainId}", _name, _grainContext.GrainId);
            }
        }

        [DoesNotReturn]
        private void OnError(Exception exception, ErrorCode id, string operation)
        {
            string? errorCode = null;
            (_store as IRestExceptionDecoder)?.DecodeException(exception, out _, out errorCode, true);
            var errorString = errorCode is { Length: > 0 } ? $" Error: {errorCode}" : null;

            var grainId = _grainContext.GrainId;
            var providerName = _store.GetType().Name;
            _logger.LogError((int)id, exception, "Error from storage provider {ProviderName}.{StateName} during {Operation} for grain {GrainId}{ErrorCode}", providerName, _name, operation, grainId, errorString);

            // If error is not specialization of OrleansException, wrap it
            if (exception is not OrleansException)
            {
                var errMsg = $"Error from storage provider {providerName}.{_name} during {operation} for grain {grainId}{errorString}{Environment.NewLine} {LogFormatter.PrintException(exception)}";
                throw new OrleansException(errMsg, exception);
            }

            ExceptionDispatchInfo.Throw(exception);
        }
    }
}
