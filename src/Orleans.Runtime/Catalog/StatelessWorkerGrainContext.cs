using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class StatelessWorkerGrainContext : IGrainContext, IAsyncDisposable, IActivationLifecycleObserver
    {
        private readonly GrainAddress _address;
        private readonly GrainTypeSharedContext _shared;
        private readonly IGrainContextActivator _innerActivator;
        private readonly int _maxWorkers;
        private readonly List<ActivationData> _workers = new();
        private readonly ConcurrentQueue<(WorkItemType Type, object State)> _workItems = new();
        private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = false };

        /// <summary>
        /// The <see cref="Task"/> representing the <see cref="RunMessageLoop"/> invocation.
        /// This is written once but never otherwise accessed. The purpose of retaining this field is for
        /// debugging, where being able to identify the message loop task corresponding to an activation can
        /// be useful.
        /// </summary>
#pragma warning disable IDE0052 // Remove unread private members
        private readonly Task _messageLoopTask;
#pragma warning restore IDE0052 // Remove unread private members
        
        private GrainReference _grainReference;

        public StatelessWorkerGrainContext(
            GrainAddress address,
            GrainTypeSharedContext sharedContext,
            IGrainContextActivator innerActivator)
        {
            _address = address;
            _shared = sharedContext;
            _innerActivator = innerActivator;
            _maxWorkers = ((StatelessWorkerPlacement)_shared.PlacementStrategy).MaxLocal;
            _messageLoopTask = Task.Run(RunMessageLoop);
            _shared.OnCreateActivation(this);
        }

        public GrainReference GrainReference => _grainReference ??= _shared.GrainReferenceActivator.CreateReference(GrainId, default);

        public GrainId GrainId => _address.GrainId;

        public object GrainInstance => null;

        public ActivationId ActivationId => _address.ActivationId;

        public GrainAddress Address => _address;

        public IServiceProvider ActivationServices => throw new NotImplementedException();

        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();

        public IWorkItemScheduler Scheduler => throw new NotImplementedException();

        public PlacementStrategy PlacementStrategy => _shared.PlacementStrategy;

        public Task Deactivated
        {
            get
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _workItems.Enqueue(new(WorkItemType.DeactivatedTask, new DeactivatedTaskWorkItemState(completion)));
                return completion.Task;
            }
        }

        public void Activate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = null)
        {
            _workItems.Enqueue(new(WorkItemType.Activate, new ActivateWorkItemState(requestContext, cancellationToken)));
            _workSignal.Signal();
        }

        public void ReceiveMessage(object message)
        {
            _workItems.Enqueue(new(WorkItemType.Message, message));
            _workSignal.Signal();
        }

        public void Deactivate(DeactivationReason deactivationReason, CancellationToken? cancellationToken = null)
        {
            _workItems.Enqueue(new(WorkItemType.Deactivate, new DeactivateWorkItemState(deactivationReason, cancellationToken)));
            _workSignal.Signal();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _workItems.Enqueue(new(WorkItemType.DisposeAsync, new DisposeAsyncWorkItemState(completion)));
                await completion.Task;
            }
            finally
            {
                _shared.OnDestroyActivation(this);
            }
        }

        public bool Equals([AllowNull] IGrainContext other) => other is not null && ActivationId.Equals(other.ActivationId);

        public TComponent GetComponent<TComponent>() where TComponent : class => this switch
        {
            TComponent contextResult => contextResult,
            _ => _shared.GetComponent<TComponent>()
        };

        public void SetComponent<TComponent>(TComponent instance) where TComponent : class => throw new ArgumentException($"Cannot set a component on a {nameof(StatelessWorkerGrainContext)}");

        public TTarget GetTarget<TTarget>() where TTarget : class => throw new NotImplementedException();

        private async Task RunMessageLoop()
        {
            while (true)
            {
                try
                {
                    while (_workItems.TryDequeue(out var workItem))
                    {
                        switch (workItem.Type)
                        {
                            case WorkItemType.Message:
                                ReceiveMessageInternal(workItem.State);
                                break;
                            case WorkItemType.Activate:
                                {
                                    var state = (ActivateWorkItemState)workItem.State;
                                    ActivateInternal(state.RequestContext, state.CancellationToken);
                                    break;
                                }
                            case WorkItemType.Deactivate:
                                {
                                    var state = (DeactivateWorkItemState)workItem.State;
                                    DeactivateInternal(state.DeactivationReason, state.CancellationToken);
                                    break;
                                }
                            case WorkItemType.DeactivatedTask:
                                {
                                    var state = (DeactivatedTaskWorkItemState)workItem.State;
                                    _ = DeactivatedTaskInternal(state.Completion);
                                    break;
                                }
                            case WorkItemType.DisposeAsync:
                                {
                                    var state = (DisposeAsyncWorkItemState)workItem.State;
                                    _ = DisposeAsyncInternal(state.Completion);
                                    break;
                                }
                            case WorkItemType.OnDestroyActivation:
                                {
                                    var grainContext = (ActivationData)workItem.State;
                                    _workers.Remove(grainContext);
                                    break;
                                }
                            default: throw new NotSupportedException($"Work item of type {workItem.Type} is not supported");
                        }
                    }

                    await _workSignal.WaitAsync();
                }
                catch (Exception exception)
                {
                    _shared.Logger.LogError(exception, "Error in stateless worker message loop");
                }
            }
        }

        private void ReceiveMessageInternal(object message)
        {
            try
            {
                ActivationData worker = null;
                ActivationData minimumWaitingCountWorker = null;
                var minimumWaitingCount = int.MaxValue;

                // Make sure there is at least one worker
                if (_workers.Count == 0)
                {
                    worker = CreateWorker(message);
                }
                else
                {
                    // Check to see if we have any inactive workers, prioritizing
                    // them in the order they were created to minimize the number
                    // of workers spawned
                    for (var i = 0; i < _workers.Count; i++)
                    {
                        if (_workers[i].IsInactive)
                        {
                            worker = _workers[i];
                            break;
                        }
                        else
                        {
                            // Track the worker with the lowest value for WaitingCount,
                            // this is used if all workers are busy
                            if (_workers[i].WaitingCount < minimumWaitingCount)
                            {
                                minimumWaitingCount = _workers[i].WaitingCount;
                                minimumWaitingCountWorker = _workers[i];
                            }
                        }
                    }

                    if (worker == null)
                    {
                        // There are no inactive workers, can we make more of them?
                        if (_workers.Count < _maxWorkers)
                        {
                            worker = CreateWorker(message);
                        }
                        else
                        {
                            // Pick the one with the lowest waiting count
                            worker = minimumWaitingCountWorker;
                        }
                    }
                }

                worker.ReceiveMessage(message);
            }
            catch (Exception exception) when (message is Message msg)
            {
                _shared.InternalRuntime.MessageCenter.RejectMessage(
                    msg,
                    Message.RejectionTypes.Transient,
                    exception,
                    "Exception while creating grain context");
            }
        }

        private ActivationData CreateWorker(object message)
        {
            var address = GrainAddress.GetAddress(_address.SiloAddress, _address.GrainId, ActivationId.NewId());
            var newWorker = (ActivationData)_innerActivator.CreateContext(address);

            // Observe the create/destroy lifecycle of the activation
            newWorker.SetComponent<IActivationLifecycleObserver>(this);

            // If this is a new worker and there is a message in scope, try to get the request context and activate the worker
            var requestContext = (message as Message)?.RequestContextData ?? new Dictionary<string, object>();
            var cancellation = new CancellationTokenSource(_shared.InternalRuntime.CollectionOptions.Value.ActivationTimeout);
            newWorker.Activate(requestContext, cancellation.Token);

            _workers.Add(newWorker);
            return newWorker;
        }

        private void ActivateInternal(Dictionary<string, object> requestContext, CancellationToken? cancellationToken)
        {
            // No-op
        }

        private void DeactivateInternal(DeactivationReason reason, CancellationToken? cancellationToken)
        {
            foreach (var worker in _workers)
            {
                worker.Deactivate(reason, cancellationToken);
            }
        }

        private async Task DeactivatedTaskInternal(TaskCompletionSource<bool> completion)
        {
            try
            {
                var tasks = new List<Task>(_workers.Count);
                foreach (var worker in _workers)
                {
                    tasks.Add(worker.Deactivated);
                }

                await Task.WhenAll(tasks);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private async Task DisposeAsyncInternal(TaskCompletionSource<bool> completion)
        {
            try
            {
                var tasks = new List<Task>(_workers.Count);
                foreach (var worker in _workers)
                {
                    try
                    {
                        if (worker is IAsyncDisposable disposable)
                        {
                            tasks.Add(disposable.DisposeAsync().AsTask());
                        }
                    }
                    catch (Exception exception)
                    {
                        tasks.Add(Task.FromException(exception));
                    }
                }

                await Task.WhenAll(tasks);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        public void OnCreateActivation(IGrainContext grainContext)
        {
        }

        public void OnDestroyActivation(IGrainContext grainContext)
        {
            _workItems.Enqueue((WorkItemType.OnDestroyActivation, grainContext));
            _workSignal.Signal();
            if (_workers.Count == 0)
            {
                _shared.InternalRuntime.Catalog.UnregisterMessageTarget(this);
            }
        }

        public void Rehydrate(IRehydrationContext context)
        {
            // Migration is not supported, but we need to dispose of the context if it's provided
            (context as IDisposable)?.Dispose();
        }

        public void Migrate(Dictionary<string, object> requestContext, CancellationToken? cancellationToken = null)
        {
            // Migration is not supported. Do nothing: the contract is that this method attempts migration, but does not guarantee it will occur.
        }

        private enum WorkItemType
        {
            Activate = 0,
            Message = 1,
            Deactivate = 2,
            DeactivatedTask = 3,
            DisposeAsync = 4,
            OnDestroyActivation = 5,
        }

        private record ActivateWorkItemState(Dictionary<string, object> RequestContext, CancellationToken? CancellationToken);
        private record DeactivateWorkItemState(DeactivationReason DeactivationReason, CancellationToken? CancellationToken);
        private record DeactivatedTaskWorkItemState(TaskCompletionSource<bool> Completion);
        private record DisposeAsyncWorkItemState(TaskCompletionSource<bool> Completion);
    }
}
