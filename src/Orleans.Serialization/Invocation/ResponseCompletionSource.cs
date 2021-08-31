using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Serialization.Invocation
{
    public sealed class ResponseCompletionSource : IResponseCompletionSource, IValueTaskSource<Response>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<Response> _core;

        public ValueTask<Response> AsValueTask() => new(this, _core.Version);

        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public void Reset()
        {
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        public void SetException(Exception exception) => _core.SetException(exception);

        public void SetResult(Response result)
        {
            if (result.Exception is not { } exception)
            {
                _core.SetResult(result);
            }
            else
            {
                _core.SetException(exception);
            }
        }
        public void Complete(Response value) => SetResult(value);
        public void Complete() => SetResult(Response.Completed);

        public Response GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }

        void IValueTaskSource.GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                _ = _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }
    }

    public sealed class ResponseCompletionSource<TResult> : IResponseCompletionSource, IValueTaskSource<TResult>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<TResult> _core;

        public ValueTask<TResult> AsValueTask() => new(this, _core.Version);

        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        public void Reset()
        {
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        public void SetException(Exception exception) => _core.SetException(exception);

        public void SetResult(TResult result) => _core.SetResult(result);

        public void Complete(Response value)
        {
            if (value is Response<TResult> typed)
            {
                Complete(typed);
            }
            else if (value.Exception is { } exception)
            {
                SetException(exception);
            }
            else
            {
                var result = value.Result;
                if (result is null)
                {
                    SetResult(default);
                }
                else if (result is TResult typedResult)
                {
                    SetResult(typedResult);
                }
                else
                {
                    SetInvalidCastException(result);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void SetInvalidCastException(object result)
        {
            var exception = new InvalidCastException($"Cannot cast object of type {result.GetType()} to {typeof(TResult)}");
#if NET5_0
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.SetCurrentStackTrace(exception);
            SetException(exception);
#else
            try
            {
                throw exception;
            }
            catch (Exception ex)
            {
                SetException(ex);
            }
#endif
        }

        /// <summary>
        /// Sets the result.
        /// </summary>
        /// <param name="value">The result value.</param>
        public void Complete(Response<TResult> value)
        {
            if (value.Exception is { } exception)
            {
                SetException(exception);
            }
            else
            {
                SetResult(value.TypedResult);
            }
        }

        public void Complete() => SetResult(default);

        public TResult GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                return _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }

        void IValueTaskSource.GetResult(short token)
        {
            bool isValid = token == _core.Version;
            try
            {
                _ = _core.GetResult(token);
            }
            finally
            {
                if (isValid)
                {
                    Reset();
                }
            }
        }
    }
}