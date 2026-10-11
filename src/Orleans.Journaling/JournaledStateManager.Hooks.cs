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
                return _hooks ??= new(_workLoop is not null || _shutdownCancellation.IsCancellationRequested);
            }
        }
    }

    private async ValueTask InvokeBeforeHooksAsync(JournaledStateOperation operation, CancellationToken cancellationToken)
    {
        for (var i = _hooks!.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _hooks[i].BeforeOperationAsync(operation, cancellationToken).ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class HookCollection(bool isReadOnly) : Collection<IJournaledStateHook>, IList<IJournaledStateHook>
    {
        private bool _isReadOnly = isReadOnly;

        bool ICollection<IJournaledStateHook>.IsReadOnly => _isReadOnly;

        public void Freeze() => _isReadOnly = true;

        protected override void InsertItem(int index, IJournaledStateHook item)
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(item);
            base.InsertItem(index, item);
        }

        protected override void SetItem(int index, IJournaledStateHook item)
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(item);
            base.SetItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            ThrowIfReadOnly();
            base.RemoveItem(index);
        }

        protected override void ClearItems()
        {
            ThrowIfReadOnly();
            base.ClearItems();
        }

        private void ThrowIfReadOnly()
        {
            if (_isReadOnly)
            {
                throw new NotSupportedException("Journal operation hook registrations are read-only.");
            }
        }
    }
}
