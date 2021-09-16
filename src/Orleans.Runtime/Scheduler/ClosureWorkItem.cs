using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal class AsyncClosureWorkItem : WorkItemBase
    {
        public static readonly Action<object> ExecuteAction = state => ((AsyncClosureWorkItem)state).Execute();

        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task> continuation;
        private readonly string name;

        public override string Name => this.name ?? GetMethodName(this.continuation);
        public Task Task => this.completion.Task;

        public AsyncClosureWorkItem(Func<Task> closure, string name, IGrainContext grainContext)
        {
            this.continuation = closure;
            this.name = name;
            this.GrainContext = grainContext;
        }

        public AsyncClosureWorkItem(Func<Task> closure, IGrainContext grainContext)
        {
            this.continuation = closure;
            this.GrainContext = grainContext;
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

        public override IGrainContext GrainContext { get; }
        
        internal static string GetMethodName(Delegate action)
        {
            var continuationMethodInfo = action.GetMethodInfo();
            return string.Format(
                "{0}->{1}",
                action.Target?.ToString() ?? string.Empty,
                continuationMethodInfo == null ? string.Empty : continuationMethodInfo.ToString());
        }
    }

    internal class AsyncClosureWorkItem<T> : WorkItemBase
    {
        public static readonly Action<object> ExecuteAction = state => ((AsyncClosureWorkItem<T>)state).Execute();

        private readonly TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task<T>> continuation;
        private readonly string name;

        public override string Name => this.name ?? AsyncClosureWorkItem.GetMethodName(this.continuation);
        public Task<T> Task => this.completion.Task;

        public AsyncClosureWorkItem(Func<Task<T>> closure, string name, IGrainContext grainContext)
        {
            this.continuation = closure;
            this.name = name;
            this.GrainContext = grainContext;
        }

        public AsyncClosureWorkItem(Func<Task<T>> closure, IGrainContext grainContext)
        {
            this.continuation = closure;
            this.GrainContext = grainContext;
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

        public override IGrainContext GrainContext { get; }
    }
}
