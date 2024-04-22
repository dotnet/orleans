#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Serializers;
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
        private readonly IGrainContext _grainContext;
        private readonly StateStorageBridgeShared<TState> _shared;
        private GrainState<TState>? _grainState;

        /// <inheritdoc/>
        public TState State
        {
            get
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                if (_grainState is { } grainState)
                {
                    return grainState.State;
                }

                return default!;
            }

            set
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                GrainState.State = value;
            }
        }

        private GrainState<TState> GrainState => _grainState ??= new GrainState<TState>(_shared.Activator.Create());
        internal bool IsStateInitialized { get; private set; }

        /// <inheritdoc/>
        public string? Etag { get => _grainState?.ETag; set => GrainState.ETag = value; }

        /// <inheritdoc/>
        public bool RecordExists => IsStateInitialized switch
        {
            true => GrainState.RecordExists,
            _ => throw new InvalidOperationException("State has not yet been loaded")
        };

        [Obsolete("Use StateStorageBridge(string, IGrainContext, IGrainStorage) instead.")]
        public StateStorageBridge(string name, IGrainContext grainContext, IGrainStorage store, ILoggerFactory loggerFactory, IActivatorProvider activatorProvider) : this(name, grainContext, store)
        { }

        public StateStorageBridge(string name, IGrainContext grainContext, IGrainStorage store)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(grainContext);
            ArgumentNullException.ThrowIfNull(store);

            _grainContext = grainContext;
            var sharedInstances = ActivatorUtilities.GetServiceOrCreateInstance<StateStorageBridgeSharedMap>(grainContext.ActivationServices);
            _shared = sharedInstances.Get<TState>(name, store);
        }

        /// <inheritdoc />
        public async Task ReadStateAsync()
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                var sw = ValueStopwatch.StartNew();
                await _shared.Store.ReadStateAsync(_shared.Name, _grainContext.GrainId, GrainState);
                IsStateInitialized = true;
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
                await _shared.Store.WriteStateAsync(_shared.Name, _grainContext.GrainId, GrainState);
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
                await _shared.Store.ClearStateAsync(_shared.Name, _grainContext.GrainId, GrainState);
                sw.Stop();

                // Reset the in-memory copy of the state
                GrainState.State = _shared.Activator.Create();

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
                dehydrationContext.TryAddValue(_shared.MigrationContextKey, _grainState);
            }
            catch (Exception exception)
            {
                _shared.Logger.LogError(exception, "Failed to dehydrate state named {StateName} for grain {GrainId}", _shared.Name, _grainContext.GrainId);

                // We must throw here since we do not know that the dehydration context is in a clean state after this.
                throw;
            }
        }

        public void OnRehydrate(IRehydrationContext rehydrationContext)
        {
            try
            {
                if (rehydrationContext.TryGetValue<GrainState<TState>>(_shared.MigrationContextKey, out var grainState))
                {
                    _grainState = grainState;
                    IsStateInitialized = true;
                }
            }
            catch (Exception exception)
            {
                // It is ok to swallow this exception, since state rehydration is best-effort.
                _shared.Logger.LogError(exception, "Failed to rehydrate state named {StateName} for grain {GrainId}", _shared.Name, _grainContext.GrainId);
            }
        }

        [DoesNotReturn]
        private void OnError(Exception exception, ErrorCode id, string operation)
        {
            string? errorCode = null;
            (_shared.Store as IRestExceptionDecoder)?.DecodeException(exception, out _, out errorCode, true);
            var errorString = errorCode is { Length: > 0 } ? $" Error: {errorCode}" : null;

            var grainId = _grainContext.GrainId;
            var providerName = _shared.Store.GetType().Name;
            _shared.Logger.LogError((int)id, exception, "Error from storage provider {ProviderName}.{StateName} during {Operation} for grain {GrainId}{ErrorCode}", providerName, _shared.Name, operation, grainId, errorString);

            // If error is not specialization of OrleansException, wrap it
            if (exception is not OrleansException)
            {
                var errMsg = $"Error from storage provider {providerName}.{_shared.Name} during {operation} for grain {grainId}{errorString}{Environment.NewLine} {LogFormatter.PrintException(exception)}";
                throw new OrleansException(errMsg, exception);
            }

            ExceptionDispatchInfo.Throw(exception);
        }
    }

    internal sealed class StateStorageBridgeSharedMap(ILoggerFactory loggerFactory, IActivatorProvider activatorProvider)
    {
        private readonly ConcurrentDictionary<(string Name, IGrainStorage Store, Type StateType), object> _instances = new();
        private readonly ILoggerFactory _loggerFactory = loggerFactory;
        private readonly IActivatorProvider _activatorProvider = activatorProvider;

        public StateStorageBridgeShared<TState> Get<TState>(string name, IGrainStorage store)
            => (StateStorageBridgeShared<TState>)_instances.GetOrAdd(
                (name, store, typeof(TState)),
                static (key, self) => new StateStorageBridgeShared<TState>(
                    key.Name,
                    key.Store,
                    self._loggerFactory.CreateLogger(key.Store.GetType()),
                    self._activatorProvider.GetActivator<TState>()),
                this);
    }

    internal sealed class StateStorageBridgeShared<TState>(string name, IGrainStorage store, ILogger logger, IActivator<TState> activator)
    {
        private string? _migrationContextKey;

        public readonly string Name = name;
        public readonly IGrainStorage Store = store;
        public readonly ILogger Logger = logger;
        public readonly IActivator<TState> Activator = activator;
        public string MigrationContextKey => _migrationContextKey ??= $"state.{Name}";
    }
}
