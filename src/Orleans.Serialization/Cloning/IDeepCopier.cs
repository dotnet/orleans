using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;

namespace Orleans.Serialization.Cloning
{
    public interface IDeepCopierProvider
    {
        IDeepCopier<T> GetDeepCopier<T>();
        IDeepCopier<T> TryGetDeepCopier<T>();
        IDeepCopier<object> GetDeepCopier(Type type);
        IDeepCopier<object> TryGetDeepCopier(Type type);
        IBaseCopier<T> GetBaseCopier<T>() where T : class;
    }
    
    public interface IDeepCopier { }

    public interface IDeepCopier<T> : IDeepCopier
    {
        T DeepCopy(T input, CopyContext context);
    }

    public interface IBaseCopier
    {
    }

    public interface IBaseCopier<T> : IBaseCopier where T : class
    {
        void DeepCopy(T input, T output, CopyContext context);
    }

    /// <summary>
    /// Indicates that an IDeepCopier implementation generalizes over all sub-types.
    /// </summary>
    public interface IDerivedTypeCopier
    {
    }

    public interface IGeneralizedCopier : IDeepCopier<object>
    {
        bool IsSupportedType(Type type);
    }

    public interface ISpecializableCopier
    {
        bool IsSupportedType(Type type);
        IDeepCopier GetSpecializedCodec(Type type);
    }

    public sealed class CopyContext : IDisposable
    {
        private readonly Dictionary<object, object> _copies = new(ReferenceEqualsComparer.Default);
        private readonly CodecProvider _copierProvider;
        private readonly Action<CopyContext> _onDisposed;

        public CopyContext(CodecProvider codecProvider, Action<CopyContext> onDisposed)
        {
            _copierProvider = codecProvider;
            _onDisposed = onDisposed;
        }

        public bool TryGetCopy<T>(object original, out T result) where T : class
        {
            if (original is null)
            {
                result = null;
                return true;
            }

            if (_copies.TryGetValue(original, out var existing))
            {
                result = existing as T;
                return true;
            }

            result = null;
            return false;
        }

        public void RecordCopy(object original, object copy)
        {
            _copies[original] = copy;
        }

        public void Reset() => _copies.Clear();

        public T Copy<T>(T value)
        {
            var copier = _copierProvider.GetDeepCopier(value.GetType());
            return (T)copier.DeepCopy(value, this);
        }

        public void Dispose() => _onDisposed?.Invoke(this);
    }

    internal static class ShallowCopyableTypes
    {
        private static readonly ConcurrentDictionary<Type, bool> Types = new()
        {
            [typeof(decimal)] = true,
            [typeof(DateTime)] = true,
            [typeof(DateTimeOffset)] = true,
            [typeof(TimeSpan)] = true,
            [typeof(IPAddress)] = true,
            [typeof(IPEndPoint)] = true,
            [typeof(string)] = true,
            [typeof(CancellationToken)] = true,
            [typeof(Guid)] = true,
        };

        public static bool Contains(Type type)
        {
            if (Types.TryGetValue(type, out var result))
            {
                return result;
            }

            return Types.GetOrAdd(type, IsShallowCopyableInternal(type));
        }

        private static bool IsShallowCopyableInternal(Type type)
        {
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }

            if (type.IsDefined(typeof(ImmutableAttribute), false))
            {
                return true;
            }

            if (type.IsConstructedGenericType)
            {
                var def = type.GetGenericTypeDefinition();

                if (def == typeof(Nullable<>)
                    || def == typeof(Tuple<>)
                    || def == typeof(Tuple<,>)
                    || def == typeof(Tuple<,,>)
                    || def == typeof(Tuple<,,,>)
                    || def == typeof(Tuple<,,,,>)
                    || def == typeof(Tuple<,,,,,>)
                    || def == typeof(Tuple<,,,,,,>)
                    || def == typeof(Tuple<,,,,,,,>))
                {
                    return Array.TrueForAll(type.GenericTypeArguments, a => Contains(a));
                }
            }

            if (type.IsValueType && !type.IsGenericTypeDefinition)
            {
                return Array.TrueForAll(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic), f => Contains(f.FieldType));
            }

            if (typeof(Exception).IsAssignableFrom(type))
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>throw new NotImplementedException();
    /// Methods for adapting typed and untyped copiers.
    /// </summary>
    internal static class CopierAdapter
    {
        /// <summary>
        /// Converts a strongly-typed copier into an untyped copier.
        /// </summary>
        public static IDeepCopier<object> CreateUntypedFromTyped<T, TCopier>(TCopier typedCodec) where TCopier : IDeepCopier<T> => new TypedCopierWrapper<T, TCopier>(typedCodec);

        /// <summary>
        /// Converts an untyped codec into a strongly-typed codec.
        /// </summary>
        public static IDeepCopier<T> CreateTypedFromUntyped<T>(IDeepCopier<object> untypedCodec) => new UntypedCopierWrapper<T>(untypedCodec);

        private sealed class TypedCopierWrapper<T, TCopier> : IDeepCopier<object>, IWrappedCodec where TCopier : IDeepCopier<T>
        {
            private readonly TCopier _copier;

            public TypedCopierWrapper(TCopier codec)
            {
                _copier = codec;
            }

            object IDeepCopier<object>.DeepCopy(object original, CopyContext context) => _copier.DeepCopy((T)original, context);

            public object Inner => _copier;
        }

        private sealed class UntypedCopierWrapper<T> : IWrappedCodec, IDeepCopier<T>
        {
            private readonly IDeepCopier<object> _codec;

            public UntypedCopierWrapper(IDeepCopier<object> codec)
            {
                _codec = codec;
            }

            public object Inner => _codec;

            T IDeepCopier<T>.DeepCopy(T original, CopyContext context) => (T)_codec.DeepCopy(original, context);
        }
    }

    public sealed class ShallowCopyableTypeCopier<T> : IDeepCopier<T>
    {
        public T DeepCopy(T input, CopyContext context) => input;
    }

    public sealed class CopyContextPool 
    {
        private readonly ObjectPool<CopyContext> _pool;

        public CopyContextPool(CodecProvider codecProvider)
        {
            var sessionPoolPolicy = new PoolPolicy(codecProvider, Return);
            _pool = new DefaultObjectPool<CopyContext>(sessionPoolPolicy);
        }

        public CopyContext GetContext() => _pool.Get();

        private void Return(CopyContext session) => _pool.Return(session);

        private class PoolPolicy : IPooledObjectPolicy<CopyContext>
        {
            private readonly CodecProvider _codecProvider;
            private readonly Action<CopyContext> _onDisposed;

            public PoolPolicy(CodecProvider codecProvider, Action<CopyContext> onDisposed)
            {
                _codecProvider = codecProvider;
                _onDisposed = onDisposed;
            }

            public CopyContext Create() => new(_codecProvider, _onDisposed);

            public bool Return(CopyContext obj)
            {
                obj.Reset();
                return true;
            }
        }
    }
}
