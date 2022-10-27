using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Orleans.Serialization.Activators;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents the result of a method invocation.
    /// </summary>
    [SerializerTransparent]
    public abstract class Response : IDisposable
    {
        /// <summary>
        /// Creates a new response representing an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns>A new response.</returns>
        public static Response FromException(Exception exception) => new ExceptionResponse { Exception = exception };

        /// <summary>
        /// Creates a new response object which has been fulfilled with the provided value.
        /// </summary>
        /// <typeparam name="TResult">The underlying result type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>A new response.</returns>
        public static Response FromResult<TResult>(TResult value)
        {
            var result = ResponsePool.Get<TResult>();
            result.TypedResult = value;
            return result;
        }

        /// <summary>
        /// Gets a completed response.
        /// </summary>
        public static Response Completed => CompletedResponse.Instance;

        /// <inheritdoc />
        public abstract object Result { get; set; }

        public virtual Type GetSimpleResultType() => null;

        /// <inheritdoc />
        public abstract Exception Exception { get; set; }

        /// <inheritdoc />
        public abstract T GetResult<T>();

        /// <inheritdoc />
        public abstract void Dispose();

        /// <inheritdoc />
        public override string ToString() => Exception is { } ex ? ex.ToString() : Result is { } r ? r.ToString() : "[null]";
    }

    /// <summary>
    /// Represents a completed <see cref="Response"/>.
    /// </summary>
    [GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
    public sealed class CompletedResponse : Response
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static CompletedResponse Instance { get; } = new CompletedResponse();

        /// <inheritdoc/>
        public override object Result { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); } 

        /// <inheritdoc/>
        public override Exception Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); }

        /// <inheritdoc/>
        public override T GetResult<T>() => default;

        /// <inheritdoc/>
        public override void Dispose() { }

        /// <inheritdoc/>
        public override string ToString() => "[Completed]";
    }

    /// <summary>
    /// Activator for <see cref="CompletedResponse"/>.
    /// </summary>
    [RegisterActivator]
    internal sealed class CompletedResponseActivator : IActivator<CompletedResponse>
    {
        /// <inheritdoc/>
        public CompletedResponse Create() => CompletedResponse.Instance;
    }

    /// <summary>
    /// A <see cref="Response"/> which represents an exception, a broken promise.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class ExceptionResponse : Response
    {
        /// <inheritdoc/>
        public override object Result
        {
            get
            {
                ExceptionDispatchInfo.Capture(Exception).Throw();
                return null;
            }

            set => throw new InvalidOperationException($"Cannot set result property on response of type {nameof(ExceptionResponse)}");
        }

        /// <inheritdoc/>
        [Id(0)]
        public override Exception Exception { get; set; }

        /// <inheritdoc/>
        public override T GetResult<T>()
        {
            ExceptionDispatchInfo.Capture(Exception).Throw();
            return default;
        }

        /// <inheritdoc/>
        public override void Dispose() { }

        /// <inheritdoc/>
        public override string ToString() => Exception?.ToString() ?? "[null]";
    }

    /// <summary>
    /// A <see cref="Response"/> which represents a typed value.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    [GenerateSerializer, UseActivator, SuppressReferenceTracking]
    public sealed class Response<TResult> : Response
    {
        [Id(0)]
        private TResult _result;

        public TResult TypedResult { get => _result; set => _result = value; }

        public override Exception Exception
        {
            get => null;
            set => throw new InvalidOperationException($"Cannot set {nameof(Exception)} property for type {nameof(Response<TResult>)}");
        }

        public override object Result
        {
            get => _result;
            set => _result = (TResult)value;
        }

        public override Type GetSimpleResultType() => typeof(TResult);

        public override T GetResult<T>()
        {
            if (typeof(TResult).IsValueType && typeof(T).IsValueType && typeof(T) == typeof(TResult))
                return Unsafe.As<TResult, T>(ref _result);

            return (T)(object)_result;
        }

        public override void Dispose()
        {
            _result = default;
            ResponsePool.Return(this);
        }

        public override string ToString() => _result is { } r ? r.ToString() : "[null]";
    }

    [RegisterActivator]
    internal sealed class PooledResponseActivator<TResult> : IActivator<Response<TResult>>
    {
        public Response<TResult> Create() => ResponsePool.Get<TResult>();
    }
}