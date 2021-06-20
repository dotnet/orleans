using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Invocation
{
    [GenerateSerializer]
    [SuppressReferenceTracking]
    public class PooledResponse<TResult> : Response<TResult>
    {
        [Id(0)]
        private TResult _result;

        public override TResult TypedResult { get => _result; set => _result = value; }

        public override Exception Exception
        {
            get => null;
            set => throw new InvalidOperationException($"Cannot set {nameof(Exception)} property for type {nameof(Response<TResult>)}");
        }

        public override object Result
        {
            get => TypedResult;
            set => TypedResult = (TResult)value;
        }

        public override T GetResult<T>()
        {
            if (typeof(T) == typeof(TResult))
            {
                return Unsafe.As<TResult, T>(ref _result);
            }

            return (T)(object)_result;
        }

#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
        public override void Dispose()
#pragma warning restore CA1816 // Dispose methods should call SuppressFinalize
        {
            TypedResult = default;
            ResponsePool.Return(this);
        }

        public override string ToString()
        {
            if (Exception is { } exception)
            {
                return exception.ToString();
            }

            if (_result is { })
            {
                return _result.ToString();
            }

            return "[null]";
        }
    }
}