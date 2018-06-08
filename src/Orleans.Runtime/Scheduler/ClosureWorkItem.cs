using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class ClosureWorkItem : WorkItemBase
    {
        private readonly Action continuation;
        private readonly string name;

        public override string Name => this.name ?? GetMethodName(this.continuation);

        public ClosureWorkItem(Action closure)
        {
            continuation = closure;
        }

        public ClosureWorkItem(Action closure, string name)
        {
            continuation = closure;
            this.name = name;
        }

        #region IWorkItem Members

        public override void Execute()
        {
            continuation();
        }

        public override WorkItemType ItemType => WorkItemType.Closure;

        #endregion

        internal static string GetMethodName(Delegate action)
        {
            var continuationMethodInfo = action.GetMethodInfo();
            return string.Format(
                "{0}->{1}",
                action.Target?.ToString() ?? string.Empty,
                continuationMethodInfo == null ? string.Empty : continuationMethodInfo.ToString());
        }
    }

    internal class AsyncClosureWorkItem : WorkItemBase
    {
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>();
        private readonly Func<Task> continuation;
        private readonly string name;

        public override string Name => this.name ?? ClosureWorkItem.GetMethodName(this.continuation);
        public Task Task => this.completion.Task;

        public AsyncClosureWorkItem(Func<Task> closure, string name = null)
        {
            this.continuation = closure;
            this.name = name;
        }

        public override async void Execute()
        {
            try
            {
                RequestContext.Clear();
                await this.continuation();
                this.completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                this.completion.TrySetException(exception);
            }
        }

        public override WorkItemType ItemType => WorkItemType.Closure;
    }

    internal class AsyncClosureWorkItem<T> : WorkItemBase
    {
        private readonly TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
        private readonly Func<Task<T>> continuation;
        private readonly string name;

        public override string Name => this.name ?? ClosureWorkItem.GetMethodName(this.continuation);
        public Task<T> Task => this.completion.Task;
        
        public AsyncClosureWorkItem(Func<Task<T>> closure, string name = null)
        {
            this.continuation = closure;
            this.name = name;
        }

        public override async void Execute()
        {
            try
            {
                RequestContext.Clear();
                var result = await this.continuation();
                this.completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                this.completion.TrySetException(exception);
            }
        }

        public override WorkItemType ItemType => WorkItemType.Closure;
    }
}
