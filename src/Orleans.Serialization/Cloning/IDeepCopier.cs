using Orleans.Serialization.Serializers;
using Orleans.Serialization.Utilities;
using Microsoft.Extensions.ObjectPool;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Threading;
using Orleans.Serialization.Invocation;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.Serialization.Cloning
{
    /// <summary>
    /// Provides <see cref="IDeepCopier{T}"/> instances.
    /// </summary>
    public interface IDeepCopierProvider
    {
        /// <summary>
        /// Gets a deep copier capable of copying instances of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The type supported by the copier.</typeparam>
        /// <returns>A deep copier capable of copying instances of type <typeparamref name="T"/>.</returns>
        IDeepCopier<T> GetDeepCopier<T>();

        /// <summary>
        /// Gets a deep copier capable of copying instances of type <typeparamref name="T"/>, or returns <see langword="null"/> if an appropriate copier was not found.
        /// </summary>
        /// <typeparam name="T">The type supported by the copier.</typeparam>
        /// <returns>A deep copier capable of copying instances of type <typeparamref name="T"/>, or <see langword="null"/> if an appropriate copier was not found.</returns>
        IDeepCopier<T> TryGetDeepCopier<T>();

        /// <summary>
        /// Gets a deep copier capable of copying instances of type <paramref name="type"/>.
        /// </summary>
        /// <param name="type">
        /// The type supported by the returned copier.
        /// </param>
        /// <returns>A deep copier capable of copying instances of type <paramref name="type"/>.</returns>
        IDeepCopier<object> GetDeepCopier(Type type);

        /// <summary>
        /// Gets a deep copier capable of copying instances of type <paramref name="type"/>, or returns <see langword="null"/> if an appropriate copier was not found.
        /// </summary>
        /// <param name="type">
        /// The type supported by the returned copier.
        /// </param>
        /// <returns>A deep copier capable of copying instances of type <paramref name="type"/>, or <see langword="null"/> if an appropriate copier was not found.</returns>
        IDeepCopier<object> TryGetDeepCopier(Type type);

        /// <summary>
        /// Gets a base type copier capable of copying instances of type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">
        /// The type supported by the returned copier.
        /// </typeparam>
        /// <returns>A base type copier capable of copying instances of type <typeparamref name="T"/>.</returns>
        IBaseCopier<T> GetBaseCopier<T>() where T : class;
    }

    /// <summary>
    /// Marker type for deep copiers.
    /// </summary>
    public interface IDeepCopier { }

    /// <summary>
    /// Provides functionality for creating clones of objects of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of objects which this instance can copy.</typeparam>
    /// <seealso cref="Orleans.Serialization.Cloning.IDeepCopier" />
    public interface IDeepCopier<T> : IDeepCopier
    {
        /// <summary>
        /// Creates a deep copy of the provided input.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="context">The context.</param>
        /// <returns>A copy of <paramref name="input"/>.</returns>
        T DeepCopy(T input, CopyContext context);
    }

    /// <summary>
    /// Marker type for base type copiers.
    /// </summary>
    public interface IBaseCopier
    {
    }

    /// <summary>
    /// Provides functionality for copying members from one object to another.
    /// </summary>
    /// <typeparam name="T">The type of objects which this instance can copy.</typeparam>
    /// <seealso cref="Orleans.Serialization.Cloning.IBaseCopier" />
    public interface IBaseCopier<T> : IBaseCopier where T : class
    {
        /// <summary>
        /// Clones members from <paramref name="input"/> and copies them to <paramref name="output"/>.
        /// </summary>
        /// <param name="input">The input.</param>
        /// <param name="output">The output.</param>
        /// <param name="context">The context.</param>
        void DeepCopy(T input, T output, CopyContext context);
    }

    /// <summary>
    /// Indicates that an <see cref="IDeepCopier"/> implementation generalizes over all sub-types.
    /// </summary>
    public interface IDerivedTypeCopier : IDeepCopier
    {
    }

    /// <summary>
    /// Provides functionality for copying objects of multiple types.
    /// </summary>
    public interface IGeneralizedCopier : IDeepCopier<object>
    {
        /// <summary>
        /// Returns a value indicating whether the provided type is supported by this implementation.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true"/> if the type is supported type by this implementation; otherwise, <see langword="false"/>.</returns>
        bool IsSupportedType(Type type);
    }

    /// <summary>
    /// Provides functionality for creating <see cref="IDeepCopier"/> instances which support a given type.
    /// </summary>
    public interface ISpecializableCopier
    {
        /// <summary>
        /// Returns a value indicating whether the provided type is supported by this implementation.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns><see langword="true"/> if the type is supported type by this implementation; otherwise, <see langword="false"/>.</returns>
        bool IsSupportedType(Type type);

        /// <summary>
        /// Gets an <see cref="IDeepCopier"/> implementation which supports the specified type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>An <see cref="IDeepCopier"/> implementation which supports the specified type.</returns>
        IDeepCopier GetSpecializedCopier(Type type);
    }

    /// <summary>
    /// Provides context for a copy operation.
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    public sealed class CopyContext : IDisposable
    {
        private readonly Dictionary<object, object> _copies = new(ReferenceEqualsComparer.Default);
        private readonly CodecProvider _copierProvider;
        private readonly Action<CopyContext> _onDisposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="CopyContext"/> class.
        /// </summary>
        /// <param name="codecProvider">The codec provider.</param>
        /// <param name="onDisposed">The action to call when this context is disposed.</param>
        public CopyContext(CodecProvider codecProvider, Action<CopyContext> onDisposed)
        {
            _copierProvider = codecProvider;
            _onDisposed = onDisposed;
        }

        /// <summary>
        /// Returns the previously recorded copy of the provided object, if it exists.
        /// </summary>
        /// <typeparam name="T">The object type.</typeparam>
        /// <param name="original">The original object.</param>
        /// <param name="result">The previously recorded copy of <paramref name="original"/>.</param>
        /// <returns><see langword="true"/> if a copy of <paramref name="original"/> has been recorded, <see langword="false"/> otherwise.</returns>
        public bool TryGetCopy<T>(object original, [NotNullWhen(true)] out T result) where T : class
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

        /// <summary>
        /// Records a copy of an object.
        /// </summary>
        /// <param name="original">The original value.</param>
        /// <param name="copy">The copy of <paramref name="original"/>.</param>
        public void RecordCopy(object original, object copy)
        {
            _copies[original] = copy;
        }

        /// <summary>
        /// Resets this instance.
        /// </summary>
        public void Reset() => _copies.Clear();

        /// <summary>
        /// Copies the provided value.
        /// </summary>
        /// <typeparam name="T">The value type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>A copy of the provided value.</returns>
        public T DeepCopy<T>(T value)
        {
            if (!typeof(T).IsValueType)
            {
                if (value is null) return default;
            }

            var copier = _copierProvider.GetDeepCopier(value.GetType());
            return (T)copier.DeepCopy(value, this);
        }

        /// <inheritdoc/>
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

    internal sealed class ShallowCopyableTypeCopier<T> : IDeepCopier<T>
    {
        public T DeepCopy(T input, CopyContext context) => input;
    }

    /// <summary>
    /// Object pool for <see cref="CopyContext"/> instances.
    /// </summary>
    public sealed class CopyContextPool 
    {
        private readonly ConcurrentObjectPool<CopyContext, PoolPolicy> _pool;

        /// <summary>
        /// Initializes a new instance of the <see cref="CopyContextPool"/> class.
        /// </summary>
        /// <param name="codecProvider">The codec provider.</param>
        public CopyContextPool(CodecProvider codecProvider)
        {
            var sessionPoolPolicy = new PoolPolicy(codecProvider, Return);
            _pool = new ConcurrentObjectPool<CopyContext, PoolPolicy>(sessionPoolPolicy);
        }

        /// <summary>
        /// Gets a <see cref="CopyContext"/>.
        /// </summary>
        /// <returns>A <see cref="CopyContext"/>.</returns>
        public CopyContext GetContext() => _pool.Get();

        /// <summary>
        /// Returns the specified copy context to the pool.
        /// </summary>
        /// <param name="context">The context.</param>
        private void Return(CopyContext context) => _pool.Return(context);

        private readonly struct PoolPolicy : IPooledObjectPolicy<CopyContext>
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
