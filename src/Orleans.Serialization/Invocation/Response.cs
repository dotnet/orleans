using System;
using System.Runtime.ExceptionServices;
using Orleans.Serialization.Activators;

namespace Orleans.Serialization.Invocation
{
    [GenerateSerializer]
    public abstract class Response : IDisposable
    {
        public static Response FromException(Exception exception) => new ExceptionResponse { Exception = exception };

        public static Response FromResult<TResult>(TResult value)
        {
            var result = ResponsePool.Get<TResult>();
            result.TypedResult = value;
            return result;
        }

        public static Response Completed => CompletedResponse.Instance;

        public abstract object Result { get; set; }

        public abstract Exception Exception { get; set; }

        public abstract T GetResult<T>();

        public abstract void Dispose();

        public virtual object GetResultOrDefault() => Exception switch { null => Result, _ => default };

        public override string ToString()
        {
            if (GetResultOrDefault() is { } result)
            {
                return result.ToString();
            }
            else if (Exception is { } exception)
            {
                return exception.ToString();
            }

            return "[null]";
        }
    }

    [GenerateSerializer]
    [Immutable]
    [UseActivator]
    public sealed class CompletedResponse : Response
    {
        public static CompletedResponse Instance { get; } = new CompletedResponse();

        public override object Result { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); } 

        public override Exception Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); }

        public override T GetResult<T>() => default;

        public override void Dispose() { }

        public override string ToString() => "[Completed]";
    }

    [RegisterActivator]
    public sealed class CompletedResponseActivator : IActivator<CompletedResponse>
    {
        public CompletedResponse Create() => CompletedResponse.Instance;
    }

    [GenerateSerializer]
    [Immutable]
    public sealed class ExceptionResponse : Response
    {
        public override object Result
        {
            get
            {
                ExceptionDispatchInfo.Capture(Exception).Throw();
                return null;
            }

            set => throw new InvalidOperationException($"Cannot set result property on response of type {nameof(ExceptionResponse)}");
        }

        [Id(0)]
        public override Exception Exception { get; set; }

        public override T GetResult<T>()
        {
            ExceptionDispatchInfo.Capture(Exception).Throw();
            return default;
        }

        public override void Dispose() { }

        public override string ToString() => Exception?.ToString() ?? "[null]";
    }

    [GenerateSerializer]
    public abstract class Response<TResult> : Response
    {
        public abstract TResult TypedResult { get; set; }

        public override string ToString()
        {
            if (Exception is { } exception)
            {
                return exception.ToString();
            }

            if (TypedResult is { } result)
            {
                return result.ToString();
            }

            return "[null]";
        }
    }
}