using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;

namespace Orleans.Journaling;

internal partial class JournaledStateManager
{
    private HookCollection? _hooks;

    public IList<IJournaledStateHook> Hooks
    {
        get
        {
            lock (_lock)
            {
                return _hooks ??= new(this);
            }
        }
    }

    private async ValueTask<IJournaledStateCaptureHook?> InvokeBeforeHooksAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        IJournaledStateCaptureHook? captureHook = null;
        for (var i = 0; i < _hooks!.Count; i++)
        {
            var hook = _hooks[i];
            if (hook is IJournaledStateCaptureHook capture)
            {
                captureHook = capture;
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await hook.BeforeOperationAsync(operation, cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return captureHook;
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

    private sealed class HookCollection(JournaledStateManager owner) : Collection<IJournaledStateHook>
    {
        protected override void InsertItem(int index, IJournaledStateHook item)
        {
            lock (owner._lock)
            {
                owner.EnsureRegistrationAllowed();
                ValidateHook(item, replacingIndex: -1);
                base.InsertItem(index, item);
            }
        }

        protected override void SetItem(int index, IJournaledStateHook item)
        {
            lock (owner._lock)
            {
                owner.EnsureRegistrationAllowed();
                ValidateHook(item, index);
                base.SetItem(index, item);
            }
        }

        protected override void RemoveItem(int index)
        {
            lock (owner._lock)
            {
                owner.EnsureRegistrationAllowed();
                base.RemoveItem(index);
            }
        }

        protected override void ClearItems()
        {
            lock (owner._lock)
            {
                owner.EnsureRegistrationAllowed();
                base.ClearItems();
            }
        }

        private void ValidateHook(IJournaledStateHook item, int replacingIndex)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item is IJournaledStateCaptureHook)
            {
                for (var i = 0; i < Count; i++)
                {
                    if (i != replacingIndex && this[i] is IJournaledStateCaptureHook)
                    {
                        throw new InvalidOperationException("A journal owner supports one final capture prerequisite hook. Inspect and deduplicate the hook list before registration.");
                    }
                }
            }
        }
    }
}
