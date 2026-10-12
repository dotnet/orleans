using System.Runtime.ExceptionServices;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    private List<IJournaledStateHook>? _hooks;

    public ValueTask AddHookAsync(IJournaledStateHook hook, int? index = null, CancellationToken cancellationToken = default) =>
        MutateHookAsync(hook, index, remove: false, cancellationToken);

    public ValueTask RemoveHookAsync(IJournaledStateHook hook, CancellationToken cancellationToken = default) =>
        MutateHookAsync(hook, index: null, remove: true, cancellationToken);

    private ValueTask MutateHookAsync(IJournaledStateHook hook, int? index, bool remove, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hook);
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        lock (_lock)
        {
            ThrowIfFenced();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            if (_workLoop is null || (!_workItemRunning && _workQueue.Count == 0))
            {
                ApplyHookMutation(hook, index, remove);
                return default;
            }

            var workItem = new HookMutationWorkItem(hook, index, remove);
            _workQueue.Enqueue(workItem);
            task = workItem.Task;
        }

        _workSignal.Signal();
        return new(task.WaitAsync(cancellationToken));
    }

    private void ApplyHookMutation(IJournaledStateHook hook, int? index, bool remove)
    {
        var hooks = _hooks ??= new();
        if (remove)
        {
            if (!hooks.Remove(hook))
            {
                throw new InvalidOperationException("The journal operation hook is not registered.");
            }
        }
        else
        {
            hooks.Insert(index ?? hooks.Count, hook);
        }
    }

    private ValueTask InvokeBeforeHooksAsync(ref int index, JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        while (index >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = _hooks![index--].BeforeOperationAsync(operation, cancellationToken);
            if (!pending.IsCompleted)
            {
                return pending;
            }

            pending.GetAwaiter().GetResult();
        }

        return default;
    }

    private async ValueTask InvokeAfterHooksAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        List<Exception>? failures = null;
        for (var i = 0; i < _hooks!.Count; i++)
        {
            try
            {
                await _hooks[i].AfterOperationAsync(operation, cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }

    private sealed class HookMutationWorkItem(IJournaledStateHook hook, int? index, bool remove) : WorkItem
    {
        public IJournaledStateHook Hook { get; } = hook;
        public int? Index { get; } = index;
        public bool Remove { get; } = remove;
    }
}
