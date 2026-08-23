using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Orleans.Serialization.Invocation;

/// <summary>
/// Provides bounded thread-local reuse for <see cref="IInvokable"/> implementations.
/// </summary>
/// <typeparam name="T">The invokable type.</typeparam>
/// <remarks>
/// A returned instance becomes available to one subsequent rental on the current thread.
/// Callers reset mutable state before returning an instance and transfer exclusive ownership to the pool.
/// </remarks>
public sealed class InvokablePool<T> : IDisposable where T : class, IInvokable
{
    private const int MaxPoolSizePerThread = 128;
    private readonly ThreadLocal<Stack<T>> _perThreadStack = new(static () => new());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryGetStack([NotNullWhen(true)] out Stack<T>? stack)
    {
        try
        {
            stack = _perThreadStack.Value;
            return stack is not null;
        }
        catch (ObjectDisposedException)
        {
            stack = null;
            return false;
        }
    }
    /// <summary>
    /// Attempts to take an instance owned by the current thread.
    /// </summary>
    /// <param name="item">The pooled instance, when available.</param>
    /// <returns><see langword="true"/> when an instance was available.</returns>
    public bool TryGet([NotNullWhen(true)] out T? item)
    {
        if (TryGetStack(out var stack))
        {
            return stack.TryPop(out item);
        }

        item = null;
        return false;
    }

    /// <summary>
    /// Makes an instance available for reuse by the current thread.
    /// </summary>
    /// <param name="item">The reset instance whose ownership is transferred to the pool.</param>
    public void Return(T item)
    {
        if (TryGetStack(out var stack) && stack.Count < MaxPoolSizePerThread)
        {
            stack.Push(item);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _perThreadStack.Dispose();
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
