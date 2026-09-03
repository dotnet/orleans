using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Diagnostics;
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
    public partial class StateStorageBridge<TState> : IStorage<TState>, IGrainMigrationParticipant
    {
        private readonly IGrainContext _grainContext;
        private readonly StorageInstruments _storageInstruments;
        private readonly StateStorageBridgeShared<TState> _shared;
        private readonly object _storageOperationLock = new();
        private GrainState<TState>? _grainState;
        private QueuedStorageOperation? _storageOperationTail;

        /// <inheritdoc/>
        public TState State
        {
            get
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                if (_grainState is { } grainState)
                {
                    return grainState.State!; // ReadStateAsync initializes state before consumers access it.
                }

                return default!; // Access before ReadStateAsync is outside the storage lifecycle contract.
            }

            set
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                GrainState.State = value;
            }
        }

        private GrainState<TState> GrainState => _grainState ??= new GrainState<TState>(_shared.Activator.Create());
        internal bool IsStateInitialized { get; private set; }

        internal string Name => _shared.Name;

        /// <inheritdoc/>
        public string? Etag { get => _grainState?.ETag; set => GrainState.ETag = value; }

        /// <inheritdoc/>
        public bool RecordExists => IsStateInitialized switch
        {
            true => GrainState.RecordExists,
            _ => throw new InvalidOperationException("State has not yet been loaded")
        };

        /// <summary>
        /// Initializes a new instance of the <see cref="StateStorageBridge{TState}"/> class.
        /// </summary>
        /// <param name="name">The state name.</param>
        /// <param name="grainContext">The grain context.</param>
        /// <param name="store">The grain storage provider.</param>
        /// <param name="loggerFactory">The logger factory retained for compatibility.</param>
        /// <param name="activatorProvider">The activator provider retained for compatibility.</param>
        [Obsolete("Use StateStorageBridge(string, IGrainContext, IGrainStorage) instead.")]
        public StateStorageBridge(string name, IGrainContext grainContext, IGrainStorage store, ILoggerFactory loggerFactory, IActivatorProvider activatorProvider) : this(name, grainContext, store)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="StateStorageBridge{TState}"/> class.
        /// </summary>
        /// <param name="name">The state name.</param>
        /// <param name="grainContext">The grain context.</param>
        /// <param name="store">The grain storage provider.</param>
        public StateStorageBridge(string name, IGrainContext grainContext, IGrainStorage store)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(grainContext);
            ArgumentNullException.ThrowIfNull(store);

            _grainContext = grainContext;
            _storageInstruments = grainContext.ActivationServices.GetRequiredService<StorageInstruments>();
            var sharedInstances = ActivatorUtilities.GetServiceOrCreateInstance<StateStorageBridgeSharedMap>(grainContext.ActivationServices);
            _shared = sharedInstances.Get<TState>(name, store);
        }

        /// <inheritdoc />
        public Task ReadStateAsync() => ReadStateInternalAsync(CancellationToken.None);

        /// <inheritdoc />
        public Task WriteStateAsync() => WriteStateInternalAsync(CancellationToken.None);

        /// <inheritdoc />
        public Task ClearStateAsync() => ClearStateInternalAsync(CancellationToken.None);

        /// <inheritdoc />
        Task IStorage.ReadStateAsync(CancellationToken cancellationToken) => ReadStateInternalAsync(cancellationToken);

        /// <inheritdoc />
        Task IStorage.WriteStateAsync(CancellationToken cancellationToken) => WriteStateInternalAsync(cancellationToken);

        /// <inheritdoc />
        Task IStorage.ClearStateAsync(CancellationToken cancellationToken) => ClearStateInternalAsync(cancellationToken);

        private Task ReadStateInternalAsync(CancellationToken cancellationToken)
        {
            GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
            QueuedStorageOperation operation;

            lock (_storageOperationLock)
            {
                var predecessor = _storageOperationTail;
                operation = new QueuedStorageOperation(StorageOperationKind.Read, cancellationToken);
                operation.SetCompletion(RunReadStorageOperationAsync(operation, predecessor));
                _storageOperationTail = operation;
            }

            return operation.Completion;
        }

        private Task WriteStateInternalAsync(CancellationToken cancellationToken)
        {
            GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
            QueuedStorageOperation operation;

            lock (_storageOperationLock)
            {
                if (!cancellationToken.CanBeCanceled
                    && _storageOperationTail is { Kind: StorageOperationKind.Write, Started: false, CanBeCanceled: false } tail)
                {
                    operation = tail;
                }
                else
                {
                    var predecessor = _storageOperationTail;
                    operation = new QueuedStorageOperation(StorageOperationKind.Write, cancellationToken);
                    operation.SetCompletion(RunWriteStorageOperationAsync(operation, predecessor));
                    _storageOperationTail = operation;
                }
            }

            return operation.Completion;
        }

        private Task ClearStateInternalAsync(CancellationToken cancellationToken)
        {
            GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
            QueuedStorageOperation operation;

            lock (_storageOperationLock)
            {
                if (!cancellationToken.CanBeCanceled
                    && _storageOperationTail is { Kind: StorageOperationKind.Clear, Started: false, CanBeCanceled: false } tail)
                {
                    operation = tail;
                }
                else
                {
                    var predecessor = _storageOperationTail;
                    operation = new QueuedStorageOperation(StorageOperationKind.Clear, cancellationToken);
                    operation.SetCompletion(RunClearStorageOperationAsync(operation, predecessor));
                    _storageOperationTail = operation;
                }
            }

            return operation.Completion;
        }

        private async Task RunWriteStorageOperationAsync(
            QueuedStorageOperation operation,
            QueuedStorageOperation? predecessor)
        {
            await Task.CompletedTask.ConfigureAwait(
                ConfigureAwaitOptions.ForceYielding |
                ConfigureAwaitOptions.ContinueOnCapturedContext);

            try
            {
                if (predecessor is not null)
                {
                    await predecessor.Completion.ConfigureAwait(
                        ConfigureAwaitOptions.SuppressThrowing |
                        ConfigureAwaitOptions.ContinueOnCapturedContext);
                }

                MarkStorageOperationStarted(operation);

                await WriteStateCoreAsync(operation.CancellationToken);
            }
            finally
            {
                ClearStorageOperationTail(operation);
            }
        }

        private async Task RunReadStorageOperationAsync(QueuedStorageOperation operation, QueuedStorageOperation? predecessor)
        {
            await Task.CompletedTask.ConfigureAwait(
                ConfigureAwaitOptions.ForceYielding |
                ConfigureAwaitOptions.ContinueOnCapturedContext);

            try
            {
                var readSatisfiedByPredecessor = false;

                if (predecessor is not null)
                {
                    await predecessor.Completion.ConfigureAwait(
                        ConfigureAwaitOptions.SuppressThrowing |
                        ConfigureAwaitOptions.ContinueOnCapturedContext);

                    readSatisfiedByPredecessor = predecessor.Completion.IsCompletedSuccessfully;
                }

                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                MarkStorageOperationStarted(operation);

                if (readSatisfiedByPredecessor)
                {
                    IsStateInitialized = true;
                }
                else
                {
                    await ReadStateCoreAsync(operation.CancellationToken);
                }
            }
            finally
            {
                ClearStorageOperationTail(operation);
            }
        }

        private async Task RunClearStorageOperationAsync(QueuedStorageOperation operation, QueuedStorageOperation? predecessor)
        {
            await Task.CompletedTask.ConfigureAwait(
                ConfigureAwaitOptions.ForceYielding |
                ConfigureAwaitOptions.ContinueOnCapturedContext);

            try
            {
                var clearSatisfiedByPredecessor = false;

                if (predecessor is not null)
                {
                    await predecessor.Completion.ConfigureAwait(
                        ConfigureAwaitOptions.SuppressThrowing |
                        ConfigureAwaitOptions.ContinueOnCapturedContext);

                    clearSatisfiedByPredecessor =
                        predecessor.Kind is StorageOperationKind.Clear &&
                        predecessor.Completion.IsCompletedSuccessfully;
                }

                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);
                MarkStorageOperationStarted(operation);

                if (!clearSatisfiedByPredecessor)
                {
                    await ClearStateCoreAsync(operation.CancellationToken);
                }
            }
            finally
            {
                ClearStorageOperationTail(operation);
            }
        }

        private void MarkStorageOperationStarted(QueuedStorageOperation operation)
        {
            lock (_storageOperationLock)
            {
                operation.Started = true;
            }
        }

        private void ClearStorageOperationTail(QueuedStorageOperation operation)
        {
            lock (_storageOperationLock)
            {
                if (ReferenceEquals(_storageOperationTail, operation))
                {
                    _storageOperationTail = null;
                }
            }
        }

        private async Task ReadStateCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                // Try to get the parent activity context from the current activity or from the activation's stored activity
                var parentContext = Activity.Current?.Context;
                if (parentContext is null && _grainContext is ActivationData activationData)
                {
                    // If we're in activation context and there's an activation activity, use it as parent
                    parentContext = activationData.GetActivationActivityContext();
                }

                using var activity = parentContext.HasValue
                    ? ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageRead, ActivityKind.Client, parentContext.Value)
                    : ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageRead, ActivityKind.Client);
                activity?.SetTag(ActivityTagKeys.GrainId, _grainContext.GrainId.ToString());
                activity?.SetTag(ActivityTagKeys.StorageProvider, _shared.ProviderTypeName);
                activity?.SetTag(ActivityTagKeys.StorageStateName, _shared.Name);
                activity?.SetTag(ActivityTagKeys.StorageStateType, _shared.StateTypeName);

                var sw = ValueStopwatch.StartNew();
                await _shared.Store.ReadStateAsync(_shared.Name, _grainContext.GrainId, GrainState, cancellationToken);
                IsStateInitialized = true;
                _storageInstruments.OnStorageRead(sw.Elapsed, _shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                _storageInstruments.OnStorageReadError(_shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
                OnError(exc, ErrorCode.StorageProvider_ReadFailed, nameof(ReadStateAsync));
            }
        }

        private async Task WriteStateCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                // Try to get the parent activity context from the current activity or from the activation's stored activity
                var parentContext = Activity.Current?.Context;
                if (parentContext is null && _grainContext is ActivationData activationData)
                {
                    parentContext = activationData.GetActivationActivityContext();
                }

                using var activity = parentContext.HasValue
                    ? ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageWrite, ActivityKind.Client, parentContext.Value)
                    : ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageWrite, ActivityKind.Client);
                activity?.SetTag(ActivityTagKeys.GrainId, _grainContext.GrainId.ToString());
                activity?.SetTag(ActivityTagKeys.StorageProvider, _shared.ProviderTypeName);
                activity?.SetTag(ActivityTagKeys.StorageStateName, _shared.Name);
                activity?.SetTag(ActivityTagKeys.StorageStateType, _shared.StateTypeName);

                var sw = ValueStopwatch.StartNew();
                await _shared.Store.WriteStateAsync(_shared.Name, _grainContext.GrainId, GrainState, cancellationToken);
                _storageInstruments.OnStorageWrite(sw.Elapsed, _shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                _storageInstruments.OnStorageWriteError(_shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
                OnError(exc, ErrorCode.StorageProvider_WriteFailed, nameof(WriteStateAsync));
            }
        }

        private async Task ClearStateCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                GrainRuntime.CheckRuntimeContext(RuntimeContext.Current);

                // Try to get the parent activity context from the current activity or from the activation's stored activity
                var parentContext = Activity.Current?.Context;
                if (parentContext is null && _grainContext is ActivationData activationData)
                {
                    parentContext = activationData.GetActivationActivityContext();
                }

                using var activity = parentContext.HasValue
                    ? ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageClear, ActivityKind.Client, parentContext.Value)
                    : ActivitySources.StorageGrainSource.StartActivity(ActivityNames.StorageClear, ActivityKind.Client);
                activity?.SetTag(ActivityTagKeys.GrainId, _grainContext.GrainId.ToString());
                activity?.SetTag(ActivityTagKeys.StorageProvider, _shared.ProviderTypeName);
                activity?.SetTag(ActivityTagKeys.StorageStateName, _shared.Name);
                activity?.SetTag(ActivityTagKeys.StorageStateType, _shared.StateTypeName);

                var sw = ValueStopwatch.StartNew();

                // Clear state in external storage
                await _shared.Store.ClearStateAsync(_shared.Name, _grainContext.GrainId, GrainState, cancellationToken);
                sw.Stop();

                // Update counters
                _storageInstruments.OnStorageDelete(sw.Elapsed, _shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exc)
            {
                _storageInstruments.OnStorageDeleteError(_shared.ProviderTypeName, _shared.Name, _shared.StateTypeName);
                OnError(exc, ErrorCode.StorageProvider_DeleteFailed, nameof(ClearStateAsync));
            }
        }

        /// <inheritdoc />
        public void OnDehydrate(IDehydrationContext dehydrationContext)
        {
            try
            {
                dehydrationContext.TryAddValue(_shared.MigrationContextKey, _grainState);
            }
            catch (Exception exception)
            {
                LogErrorOnDehydrate(_shared.Logger, exception, _shared.Name, _grainContext.GrainId);
                throw;
            }
        }

        /// <inheritdoc />
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
                LogErrorOnRehydrate(_shared.Logger, exception, _shared.Name, _grainContext.GrainId);
            }
        }

        [DoesNotReturn]
        private void OnError(Exception exception, ErrorCode id, string operation)
        {
            string? errorCode = null;
            (_shared.Store as IRestExceptionDecoder)?.DecodeException(exception, out _, out errorCode, true);
            var errorString = errorCode is { Length: > 0 } ? $" Error: {errorCode}" : null;

            var grainId = _grainContext.GrainId;
            switch (id)
            {
                case ErrorCode.StorageProvider_ReadFailed:
                    LogErrorStorageReadFailed(_shared.Logger, exception, _shared.ProviderTypeName, _shared.Name, operation, grainId, errorString);
                    break;
                case ErrorCode.StorageProvider_WriteFailed:
                    LogErrorStorageWriteFailed(_shared.Logger, exception, _shared.ProviderTypeName, _shared.Name, operation, grainId, errorString);
                    break;
                case ErrorCode.StorageProvider_DeleteFailed:
                    LogErrorStorageDeleteFailed(_shared.Logger, exception, _shared.ProviderTypeName, _shared.Name, operation, grainId, errorString);
                    break;
                default:
                    var message = $"Error from storage provider {_shared.ProviderTypeName}.{_shared.Name} during {operation} for grain {grainId}{errorString}";
                    _shared.Logger.Log(LogLevel.Error, new EventId((int)id), message, exception, static (state, _) => state);
                    break;
            }

            // If error is not specialization of OrleansException, wrap it
            if (exception is not OrleansException)
            {
                var errMsg = $"Error from storage provider {_shared.ProviderTypeName}.{_shared.Name} during {operation} for grain {grainId}{errorString}{Environment.NewLine} {LogFormatter.PrintException(exception)}";
                throw new OrleansException(errMsg, exception);
            }

            ExceptionDispatchInfo.Throw(exception);
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to dehydrate state named {StateName} for grain {GrainId}"
        )]
        private static partial void LogErrorOnDehydrate(ILogger logger, Exception exception, string stateName, GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Error,
            Message = "Failed to rehydrate state named {StateName} for grain {GrainId}"
        )]
        private static partial void LogErrorOnRehydrate(ILogger logger, Exception exception, string stateName, GrainId grainId);

        [LoggerMessage(
            EventId = (int)ErrorCode.StorageProvider_ReadFailed,
            Level = LogLevel.Error,
            Message = "Error from storage provider {ProviderName}.{StateName} during {Operation} for grain {GrainId}{ErrorCode}"
        )]
        private static partial void LogErrorStorageReadFailed(ILogger logger, Exception exception, string providerName, string stateName, string operation, GrainId grainId, string? errorCode);

        [LoggerMessage(
            EventId = (int)ErrorCode.StorageProvider_WriteFailed,
            Level = LogLevel.Error,
            Message = "Error from storage provider {ProviderName}.{StateName} during {Operation} for grain {GrainId}{ErrorCode}"
        )]
        private static partial void LogErrorStorageWriteFailed(ILogger logger, Exception exception, string providerName, string stateName, string operation, GrainId grainId, string? errorCode);

        [LoggerMessage(
            EventId = (int)ErrorCode.StorageProvider_DeleteFailed,
            Level = LogLevel.Error,
            Message = "Error from storage provider {ProviderName}.{StateName} during {Operation} for grain {GrainId}{ErrorCode}"
        )]
        private static partial void LogErrorStorageDeleteFailed(ILogger logger, Exception exception, string providerName, string stateName, string operation, GrainId grainId, string? errorCode);

        private enum StorageOperationKind
        {
            Read,
            Write,
            Clear
        }

        private sealed class QueuedStorageOperation(StorageOperationKind kind, CancellationToken cancellationToken)
        {
            private Task? _completion;

            public StorageOperationKind Kind { get; } = kind;

            public CancellationToken CancellationToken { get; } = cancellationToken;

            public bool CanBeCanceled => CancellationToken.CanBeCanceled;

            public bool Started { get; set; }

            public Task Completion =>
                _completion ?? throw new InvalidOperationException("The storage operation has not been scheduled.");

            public void SetCompletion(Task completion)
            {
                if (_completion is not null)
                {
                    throw new InvalidOperationException("The storage operation has already been scheduled.");
                }

                _completion = completion;
            }
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
        public readonly string ProviderTypeName = store.GetType().Name;
        public readonly string StateTypeName = typeof(TState).Name;
        public readonly IGrainStorage Store = store;
        public readonly ILogger Logger = logger;
        public readonly IActivator<TState> Activator = activator;
        public string MigrationContextKey => _migrationContextKey ??= $"state.{Name}";
    }
}
