using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// A fulfillable promise.
    /// </summary>
    public sealed class ResponseCompletionSource : IResponseCompletionSource, IValueTaskSource<Response>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<Response> _core;

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        public ValueTask<Response> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        /// <summary>
        /// Completes this instance with an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        public void SetException(Exception exception) => _core.SetException(exception);

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="result">The result.</param>
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

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="value">The result value.</param>
        public void Complete(Response value) => SetResult(value);

        /// <summary>
        /// Completes this instance with the default result.
        /// </summary>
        public void Complete() => SetResult(Response.Completed);

        /// <inheritdoc />
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

        /// <inheritdoc />
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

    /// <summary>
    /// A fulfillable promise.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    public sealed class ResponseCompletionSource<TResult> : IResponseCompletionSource, IValueTaskSource<TResult>, IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<TResult> _core;

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        public ValueTask<TResult> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset()
        {
            _core.Reset();
            ResponseCompletionSourcePool.Return(this);
        }

        /// <summary>
        /// Completes this instance with an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        public void SetException(Exception exception) => _core.SetException(exception);

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="result">The result.</param>
        public void SetResult(TResult result) => _core.SetResult(result);

        /// <inheritdoc/>
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
#if NET5_0_OR_GREATER
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
        /// Completes this instance with a result.
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

        /// <inheritdoc/>
        public void Complete() => SetResult(default);

        /// <inheritdoc/>
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

        /// <inheritdoc/>
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