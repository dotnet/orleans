using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class AsyncClosureWorkItem : WorkItemBase
    {
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task> continuation;
        private readonly string name;

        public override string Name => name ?? GetMethodName(continuation);
        public Task Task => completion.Task;

        public AsyncClosureWorkItem(Func<Task> closure, string name, IGrainContext grainContext)
        {
            continuation = closure;
            this.name = name;
            GrainContext = grainContext;
        }

        public AsyncClosureWorkItem(Func<Task> closure, IGrainContext grainContext)
        {
            continuation = closure;
            GrainContext = grainContext;
        }

        public override async void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(GrainContext);
                RequestContext.Clear();
                await continuation();
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
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

        public override string Name => name ?? AsyncClosureWorkItem.GetMethodName(continuation);
        public Task<T> Task => completion.Task;

        public AsyncClosureWorkItem(Func<Task<T>> closure, string name, IGrainContext grainContext)
        {
            continuation = closure;
            this.name = name;
            GrainContext = grainContext;
        }

        public AsyncClosureWorkItem(Func<Task<T>> closure, IGrainContext grainContext)
        {
            continuation = closure;
            GrainContext = grainContext;
        }

        public override async void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(GrainContext);
                RequestContext.Clear();
                var result = await continuation();
                completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                RuntimeContext.ResetExecutionContext();
            }
        }

        public override IGrainContext GrainContext { get; }
    }
}
