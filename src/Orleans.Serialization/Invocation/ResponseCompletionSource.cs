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
        // This source is pooled and GetResult returns it to the pool. Continuations must not run inline from SetResult/SetException,
        // or they can reset/reuse this instance before completion unwinds.
        private ManualResetValueTaskSourceCore<Response> _core = new() { RunContinuationsAsynchronously = true };

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        /// <remarks>The consumer owns the returned response and disposes it after use.</remarks>
        public ValueTask<Response> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        /// <remarks>Consuming the task disposes the response envelope.</remarks>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

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
        /// <param name="result">The response whose ownership is transferred to this instance.</param>
        /// <remarks>
        /// Successful responses are transferred to the result consumer. Exception responses are disposed after
        /// their exception is extracted.
        /// </remarks>
        public void SetResult(Response result)
        {
            var transferred = false;
            try
            {
                if (result.Exception is not { } exception)
                {
                    _core.SetResult(result);
                    transferred = true;
                }
                else
                {
                    _core.SetException(exception);
                }
            }
            finally
            {
                if (!transferred) result.Dispose();
            }
        }

        /// <summary>
        /// Completes this instance with a result.
        /// </summary>
        /// <param name="value">The response whose ownership is transferred to this instance.</param>
        /// <remarks>Successful envelopes remain owned by the result consumer until it finishes using them.</remarks>
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
                _core.GetResult(token).Dispose();
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
    public sealed class ResponseCompletionSource<TResult> : IResponseCompletionSource, IValueTaskSource<TResult?>, IValueTaskSource
    {
        // This source is pooled and GetResult returns it to the pool. Continuations must not run inline from SetResult/SetException,
        // or they can reset/reuse this instance before completion unwinds.
        private ManualResetValueTaskSourceCore<TResult?> _core = new() { RunContinuationsAsynchronously = true };

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask{Response}"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask{Response}"/>.</returns>
        public ValueTask<TResult?> AsValueTask() => new(this, _core.Version);

        /// <summary>
        /// Returns this instance as a <see cref="ValueTask"/>.
        /// </summary>
        /// <returns>This instance, as a <see cref="ValueTask"/>.</returns>
        public ValueTask AsVoidValueTask() => new(this, _core.Version);

        /// <inheritdoc/>
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        /// <inheritdoc/>
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);

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
        public void SetResult(TResult? result) => _core.SetResult(result);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Complete(Response value)
        {
            try
            {
                // Check exception first since it's a simple null check
                if (value.Exception is { } exception)
                {
                    SetException(exception);
                    return;
                }

                // Check for typed response (common for void returns)
                if (value is Response<TResult> typed)
                {
                    SetResult(typed.TypedResult);
                    return;
                }

                // Handle untyped successful response
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
            finally
            {
                value.Dispose();
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
        /// <param name="value">The response whose ownership is transferred to this instance.</param>
        /// <remarks>The envelope is disposed after extracting its result. The result payload remains available to the consumer.</remarks>
        public void Complete(Response<TResult> value)
        {
            try
            {
                SetResult(value.TypedResult);
            }
            finally
            {
                value.Dispose();
            }
        }

        /// <inheritdoc/>
        public void Complete() => SetResult(default);

        /// <inheritdoc/>
        public TResult? GetResult(short token)
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
