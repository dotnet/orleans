using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Serialization.Invocation
{
    [GenerateSerializer]
    public abstract class UnitTestRequestBase : IInvokable
    {
        public virtual int GetArgumentCount() => 0;
        public abstract ValueTask<Response> Invoke();
        public abstract object GetTarget();
        public abstract void SetTarget(ITargetHolder holder);
        public virtual object GetArgument(int index) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);
        public virtual void SetArgument(int index, object value) => throw new ArgumentOutOfRangeException(message: "The request has zero arguments", null);
        public abstract void Dispose();
        public abstract string GetMethodName();
        public abstract string GetInterfaceName();

        public abstract string GetActivityName();
        public abstract Type GetInterfaceType();

        public abstract MethodInfo GetMethod();
        public virtual TimeSpan? GetDefaultResponseTimeout() => null;
    }

    [GenerateSerializer]
    public abstract class UnitTestRequest : UnitTestRequestBase
    {
        [DebuggerHidden]
        public sealed override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.FromResult<object>(null));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask resultTask)
        {
            try
            {
                await resultTask;
                return Response.FromResult<object>(null);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract ValueTask InvokeInner();
    }

    [GenerateSerializer]
    public abstract class UnitTestRequest<TResult> : UnitTestRequestBase
    {
        [DebuggerHidden]
        public sealed override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.Result));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(ValueTask<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract ValueTask<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class UnitTestTaskRequest<TResult> : UnitTestRequestBase
    {
        [DebuggerHidden]
        public sealed override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    return new ValueTask<Response>(Response.FromResult(resultTask.GetAwaiter().GetResult()));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(Task<TResult> resultTask)
        {
            try
            {
                var result = await resultTask;
                return Response.FromResult(result);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract Task<TResult> InvokeInner();
    }

    [GenerateSerializer]
    public abstract class UnitTestTaskRequest : UnitTestRequestBase
    {
        [DebuggerHidden]
        public sealed override ValueTask<Response> Invoke()
        {
            try
            {
                var resultTask = InvokeInner();
                var status = resultTask.Status;
                if (resultTask.IsCompleted)
                {
                    resultTask.GetAwaiter().GetResult();
                    return new ValueTask<Response>(Response.FromResult<object>(null));
                }

                return CompleteInvokeAsync(resultTask);
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        [DebuggerHidden]
        private static async ValueTask<Response> CompleteInvokeAsync(Task resultTask)
        {
            try
            {
                await resultTask;
                return Response.FromResult<object>(null);
            }
            catch (Exception exception)
            {
                return Response.FromException(exception);
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract Task InvokeInner();
    }

    [GenerateSerializer]
    public abstract class UnitTestVoidRequest : UnitTestRequestBase
    {
        [DebuggerHidden]
        public sealed override ValueTask<Response> Invoke()
        {
            try
            {
                InvokeInner();
                return new ValueTask<Response>(Response.FromResult<object>(null));
            }
            catch (Exception exception)
            {
                return new ValueTask<Response>(Response.FromException(exception));
            }
        }

        // Generated
        [DebuggerHidden]
        protected abstract void InvokeInner();
    }
}