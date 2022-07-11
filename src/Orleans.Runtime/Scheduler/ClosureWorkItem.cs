using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class AsyncClosureWorkItem : WorkItemBase
    {
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
                RuntimeContext.SetExecutionContext(this.GrainContext);
                RequestContext.Clear();
                await this.continuation();
                this.completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                this.completion.TrySetException(exception);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override IGrainContext GrainContext { get; }

        internal static string GetMethodName(Delegate action) => $"{action.Target}->{action.Method}";
    }

    internal sealed class AsyncClosureWorkItem<T> : WorkItemBase
    {
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
                RuntimeContext.SetExecutionContext(this.GrainContext);
                RequestContext.Clear();
                var result = await this.continuation();
                this.completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                this.completion.TrySetException(exception);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override IGrainContext GrainContext { get; }
    }
}
