#nullable enable
using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Scheduler
{
    internal sealed class AsyncClosureWorkItem : WorkItemBase
    {
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task> continuation;
        private readonly string? name;

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

        internal static string GetMethodName(Delegate action) => $"{action.Target}->{action.Method}";
    }

    internal sealed class AsyncClosureWorkItem<T> : WorkItemBase
    {
        private readonly TaskCompletionSource<T> completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<Task<T>> continuation;
        private readonly string? name;

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

    internal sealed class ClosureWorkItem<TState>(Action<TState> closure, TState state, string? name, IGrainContext grainContext) : WorkItemBase
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string Name => name ?? AsyncClosureWorkItem.GetMethodName(closure);
        public Task Task => _completion.Task;

        public override void Execute()
        {
            try
            {
                RequestContext.Clear();
                closure(state);
                _completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public override IGrainContext GrainContext { get; } = grainContext;
    }

    internal sealed class StatefulAsyncClosureWorkItem<TState> : WorkItemBase
    {
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<TState, ValueTask> _continuation;
        private readonly TState _state;
        private readonly string? _name;

        public override string Name => _name ?? AsyncClosureWorkItem.GetMethodName(_continuation);
        public Task Task => _completion.Task;

        public StatefulAsyncClosureWorkItem(Func<TState, ValueTask> closure, TState state, IGrainContext grainContext)
        {
            _continuation = closure;
            _state = state;
            GrainContext = grainContext;
        }

        public StatefulAsyncClosureWorkItem(Func<TState, ValueTask> closure, TState state, string name, IGrainContext grainContext)
        {
            _continuation = closure;
            _state = state;
            _name = name;
            GrainContext = grainContext;
        }

        public override async void Execute()
        {
            try
            {
                RequestContext.Clear();
                await _continuation(_state);
                _completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public override IGrainContext GrainContext { get; }
    }
}
