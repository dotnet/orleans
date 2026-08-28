using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Provides bounded concurrent reuse for <see cref="IInvokable"/> implementations.
/// </summary>
/// <typeparam name="T">The invokable type.</typeparam>
/// <remarks>
/// A returned instance becomes available to one subsequent rental.
/// Callers reset mutable state before returning an instance and transfer exclusive ownership to the pool.
/// </remarks>
public sealed class InvokablePool<T> : IDisposable where T : class, IInvokable
{
    private const int MaxPoolSize = 128;
    private readonly T?[] _items = new T?[MaxPoolSize];
    private int _disposed;

    /// <summary>
    /// Attempts to take an instance.
    /// </summary>
    /// <param name="item">The pooled instance, when available.</param>
    /// <returns><see langword="true"/> when an instance was available.</returns>
    public bool TryGet([NotNullWhen(true)] out T? item)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            item = null;
            return false;
        }

        var start = Environment.CurrentManagedThreadId & (MaxPoolSize - 1);
        for (var offset = 0; offset < MaxPoolSize; offset++)
        {
            var index = (start + offset) & (MaxPoolSize - 1);
            if (Volatile.Read(ref _items[index]) is null)
            {
                continue;
            }

            if (Interlocked.Exchange(ref _items[index], null) is { } candidate)
            {
                item = candidate;
                return true;
            }
        }

        item = null;
        return false;
    }

    /// <summary>
    /// Makes an instance available for reuse.
    /// </summary>
    /// <param name="item">The reset instance whose ownership is transferred to the pool.</param>
    public void Return(T item)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var start = Environment.CurrentManagedThreadId & (MaxPoolSize - 1);
        for (var offset = 0; offset < MaxPoolSize; offset++)
        {
            var index = (start + offset) & (MaxPoolSize - 1);
            if (Volatile.Read(ref _items[index]) is not null)
            {
                continue;
            }

            if (Interlocked.CompareExchange(ref _items[index], item, null) is null)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    Interlocked.Exchange(ref _items[index], null);
                }

                return;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            for (var i = 0; i < _items.Length; i++)
            {
                Interlocked.Exchange(ref _items[i], null);
            }
        }
    }
}

/// <summary>
/// Provides process-wide compatibility pooling for parameterless <see cref="IInvokable"/> implementations.
/// </summary>
public static class InvokablePool
{
    /// <summary>
    /// Gets a value from the pool.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <returns>A value from the pool.</returns>
    public static T Get<T>() where T : class, IInvokable, new() => TypedPool<T>.Pool.Get();

    /// <summary>
    /// Returns a value to the pool.
    /// </summary>
    /// <typeparam name="T">The type of the value.</typeparam>
    /// <param name="obj">The value to return.</param>
    public static void Return<T>(T obj) where T : class, IInvokable, new() => TypedPool<T>.Pool.Return(obj);

    private static class TypedPool<T> where T : class, IInvokable, new()
    {
        public static readonly ConcurrentObjectPool<T> Pool = new();
    }
}
