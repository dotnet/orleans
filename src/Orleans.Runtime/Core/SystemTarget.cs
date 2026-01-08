#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;
using Orleans.Serialization.Invocation;

namespace Orleans.Runtime
{
    /// <summary>
    /// Base class for various system services, such as grain directory, reminder service, etc.
    /// Made public for GrainService to inherit from it.
    /// Can be turned to internal after a refactoring that would remove the inheritance relation.
    /// </summary>
    public abstract partial class SystemTarget : ISystemTarget, ISystemTargetBase, IGrainContext, IGrainExtensionBinder, ISpanFormattable, IDisposable, IGrainTimerRegistry, IGrainCallCancellationExtension
    {
        private readonly Func<object?, Task> _handleRequest;
        private readonly SystemTargetGrainId _id;
        private readonly SystemTargetShared _shared;
        private readonly HashSet<IGrainTimer> _timers = [];
        private readonly Dictionary<Message, Task> _runningRequests = [];
        private readonly Dictionary<Type, object> _components = [];


        /// <summary>Silo address of the system target.</summary>
        public SiloAddress Silo => _shared.SiloAddress;
        internal GrainAddress ActivationAddress { get; }

        internal ActivationId ActivationId { get; set; }
        private readonly ILogger _logger;

        internal InsideRuntimeClient RuntimeClient => _shared.RuntimeClient;

        /// <inheritdoc/>
        public GrainReference GrainReference { get => field ??= _shared.GrainReferenceActivator.CreateReference(_id.GrainId, default); private set; }

        /// <inheritdoc/>
        public GrainId GrainId => _id.GrainId;

        /// <inheritdoc/>
        object IGrainContext.GrainInstance => this;

        /// <inheritdoc/>
        ActivationId IGrainContext.ActivationId => ActivationId;

        /// <inheritdoc/>
        GrainAddress IGrainContext.Address => ActivationAddress;

        private RuntimeMessagingTrace MessagingTrace => _shared.MessagingTrace;

        /// <summary>
        /// Obsolete parameterless constructor for <see cref="SystemTarget"/>.
        /// <para>
        /// <b>Breaking change:</b> This constructor is obsolete and will throw a <see cref="NotSupportedException"/> if called.
        /// It is present only to satisfy certain reflection-based scenarios and should not be used in application code.
        /// </para>
        /// <para>
        /// If you are using reflection or serialization frameworks that require a parameterless constructor,
        /// update your code to use the constructor with parameters: <see cref="SystemTarget(SystemTargetGrainId, SystemTargetShared)"/>.
        /// </para>
        /// </summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        [Obsolete("Do not call the empty constructor.")]
        protected SystemTarget() => throw new NotSupportedException();

        internal SystemTarget(GrainType grainType, SystemTargetShared shared)
            : this(SystemTargetGrainId.Create(grainType, shared.SiloAddress), shared)
        {
        }

        internal SystemTarget(SystemTargetGrainId grainId, SystemTargetShared shared)
        {
            _id = grainId;
            _shared = shared;
            _handleRequest = state => this.HandleNewRequest((Message)state!);
            ActivationId = ActivationId.GetDeterministic(grainId.GrainId);
            ActivationAddress = GrainAddress.GetAddress(Silo, _id.GrainId, ActivationId);
            _logger = shared.LoggerFactory.CreateLogger(GetType());
            WorkItemGroup = _shared.CreateWorkItemGroup(this);
            if (!Constants.IsSingletonSystemTarget(GrainId.Type))
            {
                GrainInstruments.IncrementSystemTargetCounts(Constants.SystemTargetName(GrainId.Type));
            }
        }

        internal WorkItemGroup WorkItemGroup { get; }

        /// <inheritdoc />
        public IServiceProvider ActivationServices => RuntimeClient.ServiceProvider;

        /// <inheritdoc />
        IGrainLifecycle IGrainContext.ObservableLifecycle => throw new NotImplementedException("IGrainContext.ObservableLifecycle is not implemented by SystemTarget");

        /// <inheritdoc />
        public IWorkItemScheduler Scheduler => WorkItemGroup;

        /// <summary>
        /// Gets the component with the specified type.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <returns>The component with the specified type.</returns>
        public TComponent? GetComponent<TComponent>()
        {
            TComponent? result;
            if (this is TComponent instanceResult)
            {
                result = instanceResult;
            }
            else if (_components.TryGetValue(typeof(TComponent), out var resultObj))
            {
                result = (TComponent)resultObj;
            }
            else if (typeof(TComponent) == typeof(PlacementStrategy))
            {
                result = (TComponent)(object)SystemTargetPlacementStrategy.Instance;
            }
            else
            {
                result = default;
            }

            return result;
        }

        /// <inheritdoc />
        public void SetComponent<TComponent>(TComponent? instance) where TComponent : class
        {
            if (this is TComponent)
            {
                throw new ArgumentException("Cannot override a component which is implemented by this grain");
            }

            if (instance == null)
            {
                _components.Remove(typeof(TComponent));
                return;
            }

            _components[typeof(TComponent)] = instance;
        }

        internal async Task HandleNewRequest(Message request)
        {
            try
            {
                await RuntimeClient.Invoke(this, request);
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                lock (_runningRequests)
                {
                    _runningRequests.Remove(request);
                }
            }
        }

        internal void HandleResponse(Message response)
        {
            RuntimeClient.ReceiveResponse(response);
        }

        /// <summary>
        /// Registers a timer to send regular callbacks to this system target.
        /// </summary>
        /// <param name="callback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="state">The state object passed to the callback.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="callback"/> is invoked.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="callback"/>.
        /// Specify <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        public IGrainTimer RegisterTimer(Func<object?, Task> callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = _shared.TimerRegistry
                .RegisterGrainTimer(this, static (state, _) => state.Callback(state.State), (Callback: callback, State: state), new() { DueTime = dueTime, Period = period, Interleave = true });
            return timer;
        }

        /// <summary>
        /// Registers a timer to send regular callbacks to this system target.
        /// </summary>
        /// <param name="callback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="callback"/> is invoked.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="callback"/>.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        public IGrainTimer RegisterGrainTimer(Func<CancellationToken, Task> callback, TimeSpan dueTime, TimeSpan period)
        {
            CheckRuntimeContext();
            ArgumentNullException.ThrowIfNull(callback);
            var timer = _shared.TimerRegistry
                .RegisterGrainTimer(this, (state, ct) => state(ct), callback, new() { DueTime = dueTime, Period = period, Interleave = true });
            return timer;
        }

        /// <summary>
        /// Registers a timer to send regular callbacks to this grain.
        /// This timer will keep the current grain from being deactivated.
        /// </summary>
        /// <param name="callback">The timer callback, which will fire whenever the timer becomes due.</param>
        /// <param name="state">The state object passed to the callback.</param>
        /// <param name="dueTime">
        /// The amount of time to delay before the <paramref name="callback"/> is invoked.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to prevent the timer from starting.
        /// Specify <see cref="TimeSpan.Zero"/> to invoke the callback promptly.
        /// </param>
        /// <param name="period">
        /// The time interval between invocations of <paramref name="callback"/>.
        /// Specify <see cref="Timeout.InfiniteTimeSpan"/> to disable periodic signaling.
        /// </param>
        /// <returns>
        /// An <see cref="IDisposable"/> object which will cancel the timer upon disposal.
        /// </returns>
        public IGrainTimer RegisterGrainTimer<TState>(Func<TState, CancellationToken, Task> callback, TState state, TimeSpan dueTime, TimeSpan period)
        {
            CheckRuntimeContext();
            ArgumentNullException.ThrowIfNull(callback);
            var timer = _shared.TimerRegistry
                .RegisterGrainTimer(this, callback, state, new() { DueTime = dueTime, Period = period, Interleave = true });
            return timer;
        }

        /// <inheritdoc/>
        public sealed override string ToString() => $"{this}";

        string IFormattable.ToString(string? format, IFormatProvider? formatProvider) => ToString();

        bool ISpanFormattable.TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
            => destination.TryWrite($"[SystemTarget: {Silo}/{_id}{ActivationId}]", out charsWritten);

        /// <summary>Adds details about message currently being processed</summary>
        internal string ToDetailedString()
        {
            lock (_runningRequests)
            {
                return $"{this} CurrentlyExecuting={_runningRequests}";
            }
        }

        /// <inheritdoc/>
        bool IEquatable<IGrainContext>.Equals(IGrainContext? other) => ReferenceEquals(this, other);

        /// <inheritdoc/>
        public (TExtension, TExtensionInterface) GetOrSetExtension<TExtension, TExtensionInterface>(Func<TExtension> newExtensionFunc)
            where TExtension : class, TExtensionInterface
            where TExtensionInterface : class, IGrainExtension
        {
            TExtension implementation;
            if (GetComponent<TExtensionInterface>() is object existing)
            {
                if (existing is TExtension typedResult)
                {
                    implementation = typedResult;
                }
                else
                {
                    throw new InvalidCastException($"Cannot cast existing extension of type {existing.GetType()} to target type {typeof(TExtension)}");
                }
            }
            else
            {
                implementation = newExtensionFunc();
                SetComponent<TExtensionInterface>(implementation);
            }

            var reference = GrainReference.Cast<TExtensionInterface>();
            return (implementation, reference);
        }

        /// <inheritdoc/>
        object? ITargetHolder.GetComponent(Type componentType)
        {
            object? result;
            if (componentType.IsAssignableFrom(GetType()))
            {
                result = this;
            }
            else if (_components.TryGetValue(componentType, out var resultObj))
            {
                result = resultObj;
            }
            else if (componentType == typeof(PlacementStrategy))
            {
                result = SystemTargetPlacementStrategy.Instance;
            }
            else if (typeof(IGrainExtension).IsAssignableFrom(componentType))
            {
                var implementation = ActivationServices.GetKeyedService<IGrainExtension>(componentType);
                if (implementation is null)
                {
                    throw new GrainExtensionNotInstalledException($"No extension of type {componentType} is installed on this instance and no implementations are registered for automated install");
                }

                _components[componentType] = implementation;
                result = implementation;
            }
            else
            {
                result = default;
            }

            return result;
        }

        /// <inheritdoc/>
        public TExtensionInterface GetExtension<TExtensionInterface>()
            where TExtensionInterface : class, IGrainExtension
        {
            if (GetComponent<TExtensionInterface>() is TExtensionInterface result)
            {
                return result;
            }

            var implementation = ActivationServices.GetKeyedService<IGrainExtension>(typeof(TExtensionInterface));
            if (!(implementation is TExtensionInterface typedResult))
            {
                throw new GrainExtensionNotInstalledException($"No extension of type {typeof(TExtensionInterface)} is installed on this instance and no implementations are registered for automated install");
            }

            SetComponent<TExtensionInterface>(typedResult);
            return typedResult;
        }

        /// <inheritdoc/>
        public void ReceiveMessage(object message)
        {
            var msg = (Message)message;
            switch (msg.Direction)
            {
                case Message.Directions.Request:
                case Message.Directions.OneWay:
                    {
                        MessagingTrace.OnEnqueueMessageOnActivation(msg, this);
                        using var suppressExecutionContext = new ExecutionContextSuppressor();
                        var task = new Task<Task>(_handleRequest, msg);
                        lock (_runningRequests)
                        {
                            _runningRequests[msg] = task;
                        }

                        task.Start(WorkItemGroup.TaskScheduler);
                        break;
                    }

                default:
                    LogInvalidMessage(_logger, msg);
                    break;
            }
        }

        /// <inheritdoc/>
        public object? GetTarget() => this;

        /// <inheritdoc/>
        public void Activate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken) { }

        /// <inheritdoc/>
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken) { }

        /// <inheritdoc/>
        public Task Deactivated => Task.CompletedTask;

        public void Dispose()
        {
            if (!Constants.IsSingletonSystemTarget(GrainId.Type))
            {
                GrainInstruments.DecrementSystemTargetCounts(Constants.SystemTargetName(GrainId.Type));
            }

            StopAllTimers();
        }

        public void Rehydrate(IRehydrationContext context)
        {
            // Migration is not supported, but we need to dispose of the context if it's provided
            (context as IDisposable)?.Dispose();
        }

        public void Migrate(Dictionary<string, object>? requestContext, CancellationToken cancellationToken)
        {
            // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
        }

        void IGrainTimerRegistry.OnTimerCreated(IGrainTimer timer) { lock (_timers) { _timers.Add(timer); } }
        void IGrainTimerRegistry.OnTimerDisposed(IGrainTimer timer) { lock (_timers) { _timers.Remove(timer); } }
        private void StopAllTimers()
        {
            List<IGrainTimer> timers;
            lock (_timers)
            {
                timers = [.. _timers];
                _timers.Clear();
            }

            foreach (var timer in timers)
            {
                timer.Dispose();
            }
        }

        internal void CheckRuntimeContext()
        {
            var context = RuntimeContext.Current;
            if (context is null)
            {
                ThrowMissingContext();
                void ThrowMissingContext() => throw new InvalidOperationException($"Access violation: attempted to access context '{this}' from null context.");
            }

            if (!ReferenceEquals(context, this))
            {
                ThrowAccessViolation(context);
                void ThrowAccessViolation(IGrainContext? currentContext) => throw new InvalidOperationException($"Access violation: attempt to access context '{this}' from different context, '{currentContext}'.");
            }
        }

        [LoggerMessage(
            Level = LogLevel.Error,
            EventId = (int)ErrorCode.Runtime_Error_100097,
            Message = "Invalid message: {Message}"
        )]
        private static partial void LogInvalidMessage(ILogger logger, Message Message);

        ValueTask IGrainCallCancellationExtension.CancelRequestAsync(GrainId senderGrainId, CorrelationId messageId)
            => this.RunOrQueueTask(static state => state.self.CancelRequestAsyncCore(state.senderGrainId, state.messageId), (self: this, senderGrainId, messageId));

        private ValueTask CancelRequestAsyncCore(GrainId senderGrainId, CorrelationId messageId)
        {
            if (!TryCancelRequest())
            {
                // The message being canceled may not have arrived yet, so retry a few times.
                return RetryCancellationAfterDelay();
            }

            return ValueTask.CompletedTask;

            async ValueTask RetryCancellationAfterDelay()
            {
                var attemptsRemaining = 3;
                do
                {
                    await Task.Delay(1_000);

                    if (TryCancelRequest())
                    {
                        return;
                    }
                } while (--attemptsRemaining > 0);
            }

            bool TryCancelRequest()
            {
                Message? message = null;

                lock (_runningRequests)
                {
                    // Check the running requests.
                    foreach (var runningRequest in _runningRequests.Keys)
                    {
                        if (runningRequest.Id == messageId && runningRequest.SendingGrain == senderGrainId)
                        {
                            message = runningRequest;
                            break;
                        }
                    }
                }

                var didCancel = false;
                if (message is not null)
                {
                    if (message.BodyObject is IInvokable invokableRequest)
                    {
                        didCancel = TryCancelInvokable(invokableRequest) || !invokableRequest.IsCancellable;
                    }
                    else
                    {
                        // Assume the request is not cancellable.
                        didCancel = true;
                    }
                }

                return didCancel;
            }

            bool TryCancelInvokable(IInvokable request)
            {
                try
                {
                    return request.TryCancel();
                }
                catch (Exception exception)
                {
                    LogErrorCancellationCallbackFailed(_logger, exception);
                    return true;
                }
            }
        }

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "One or more cancellation callbacks failed."
        )]
        private static partial void LogErrorCancellationCallbackFailed(ILogger logger, Exception exception);
    }
}
