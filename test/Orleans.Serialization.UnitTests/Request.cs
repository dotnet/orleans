using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

namespace Orleans.Serialization.Invocation
{
    public abstract class UnitTestRequest : IInvokable
    {
        public abstract int ArgumentCount { get; }

        [DebuggerHidden]
        public ValueTask<Response> Invoke()
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
        public abstract TTarget GetTarget<TTarget>();
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;
        public abstract TArgument GetArgument<TArgument>(int index);
        public abstract void SetArgument<TArgument>(int index, in TArgument value);
        public abstract void Dispose();
        public abstract string MethodName { get; }
        public abstract Type[] MethodTypeArguments { get; }
        public abstract string InterfaceName { get; }
        public abstract string ActivityName { get; }
        public abstract Type InterfaceType { get; }
        public abstract Type[] InterfaceTypeArguments { get; }
        public abstract Type[] ParameterTypes { get; }
        public abstract MethodInfo Method { get; }
    }

    public abstract class UnitTestRequest<TResult> : IInvokable
    {
        public abstract int ArgumentCount { get; }

        [DebuggerHidden]
        public ValueTask<Response> Invoke()
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
        public abstract TTarget GetTarget<TTarget>();
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;
        public abstract TArgument GetArgument<TArgument>(int index);
        public abstract void SetArgument<TArgument>(int index, in TArgument value);
        public abstract void Dispose();
        public abstract string MethodName { get; }
        public abstract Type[] MethodTypeArguments { get; }
        public abstract string InterfaceName { get; }
        public abstract string ActivityName { get; }
        public abstract Type InterfaceType { get; }
        public abstract Type[] InterfaceTypeArguments { get; }
        public abstract Type[] ParameterTypes { get; }
        public abstract MethodInfo Method { get; }
    }

    public abstract class UnitTestTaskRequest<TResult> : IInvokable
    {
        public abstract int ArgumentCount { get; }

        [DebuggerHidden]
        public ValueTask<Response> Invoke()
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
        public abstract TTarget GetTarget<TTarget>();
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;
        public abstract TArgument GetArgument<TArgument>(int index);
        public abstract void SetArgument<TArgument>(int index, in TArgument value);
        public abstract void Dispose();
        public abstract string MethodName { get; }
        public abstract Type[] MethodTypeArguments { get; }
        public abstract string InterfaceName { get; }
        public abstract string ActivityName { get; }
        public abstract Type InterfaceType { get; }
        public abstract Type[] InterfaceTypeArguments { get; }
        public abstract Type[] ParameterTypes { get; }
        public abstract MethodInfo Method { get; }
    }

    public abstract class UnitTestTaskRequest : IInvokable
    {
        public abstract int ArgumentCount { get; }

        [DebuggerHidden]
        public ValueTask<Response> Invoke()
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
        public abstract TTarget GetTarget<TTarget>();
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;
        public abstract TArgument GetArgument<TArgument>(int index);
        public abstract void SetArgument<TArgument>(int index, in TArgument value);
        public abstract void Dispose();
        public abstract string MethodName { get; }
        public abstract Type[] MethodTypeArguments { get; }
        public abstract string InterfaceName { get; }
        public abstract string ActivityName { get; }
        public abstract Type InterfaceType { get; }
        public abstract Type[] InterfaceTypeArguments { get; }
        public abstract Type[] ParameterTypes { get; }
        public abstract MethodInfo Method { get; }
    }

    public abstract class UnitTestVoidRequest : IInvokable
    {
        public abstract int ArgumentCount { get; }

        [DebuggerHidden]
        public ValueTask<Response> Invoke()
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
        public abstract TTarget GetTarget<TTarget>();
        public abstract void SetTarget<TTargetHolder>(TTargetHolder holder) where TTargetHolder : ITargetHolder;
        public abstract TArgument GetArgument<TArgument>(int index);
        public abstract void SetArgument<TArgument>(int index, in TArgument value);
        public abstract void Dispose();
        public abstract string MethodName { get; }
        public abstract Type[] MethodTypeArguments { get; }
        public abstract string InterfaceName { get; }
        public abstract string ActivityName { get; }
        public abstract Type InterfaceType { get; }
        public abstract Type[] InterfaceTypeArguments { get; }
        public abstract Type[] ParameterTypes { get; }
        public abstract MethodInfo Method { get; }
    }
}