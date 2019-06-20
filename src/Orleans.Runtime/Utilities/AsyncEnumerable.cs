using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Utilities
{
    internal static class AsyncEnumerable
    {
        internal static readonly object InitialValue = new object();
        internal static readonly object DisposedValue = new object();
    }

    internal sealed class AsyncEnumerable<T> : IAsyncEnumerable<T>
    {
        private enum PublishResult
        {
            Success,
            InvalidUpdate,
            Disposed
        }

        private readonly object updateLock = new object();
        private readonly Func<T, T, bool> updateValidator;
        private Element current;
        
        public AsyncEnumerable(Func<T, T, bool> updateValidator, T initial)
        {
            this.updateValidator = updateValidator;
            this.current = new Element(initial);
        }

        public Action<T> OnPublished { get; set; }

        public bool TryPublish(T value) => this.TryPublish(new Element(value)) == PublishResult.Success;
        
        public void Publish(T value)
        {
            switch (this.TryPublish(new Element(value)))
            {
                case PublishResult.Success:
                    return;
                case PublishResult.InvalidUpdate:
                    ThrowInvalidUpdate();
                    break;
                case PublishResult.Disposed:
                    ThrowDisposed();
                    break;
            }
        }

        private PublishResult TryPublish(Element newItem)
        {
            if (this.current.IsDisposed) return PublishResult.Disposed;

            lock (this.updateLock)
            {
                if (this.current.IsDisposed) return PublishResult.Disposed;

                if (this.current.IsValid && newItem.IsValid && !this.updateValidator(this.current.Value, newItem.Value))
                {
                    return PublishResult.InvalidUpdate;
                }

                var curr = this.current;
                Interlocked.Exchange(ref this.current, newItem);
                if (newItem.IsValid) this.OnPublished?.Invoke(newItem.Value);
                curr.SetNext(newItem);

                return PublishResult.Success;
            }
        }

        public void Dispose()
        {
            if (this.current.IsDisposed) return;

            lock (this.updateLock)
            {
                if (this.current.IsDisposed) return;

                this.TryPublish(Element.CreateDisposed());
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidUpdate() => throw new ArgumentException("The value was not valid");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDisposed() => throw new ObjectDisposedException("This instance has been disposed");

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumerator(this.current, cancellationToken);
        }

        private sealed class AsyncEnumerator : IAsyncEnumerator<T>
        {
            private readonly Task cancellation;
            private Element current;

            public AsyncEnumerator(Element initial, CancellationToken cancellation)
            {
                if (!initial.IsValid) this.current = initial;
                else
                {
                    var result = Element.CreateInitial();
                    result.SetNext(initial);
                    this.current = result;
                }

                if (cancellation != default)
                {
                    this.cancellation = cancellation.WhenCancelled();
                }
            }

            T IAsyncEnumerator<T>.Current => this.current.Value;

            async ValueTask<bool> IAsyncEnumerator<T>.MoveNextAsync()
            {
                Task<Element> next;
                if (this.cancellation != default)
                {
                    next = this.current.NextAsync();
                    var result = await Task.WhenAny(this.cancellation, next);
                    if (ReferenceEquals(result, this.cancellation)) return false;
                }
                else
                {
                    next = this.current.NextAsync();
                }

                this.current = await next;
                return this.current.IsValid;
            }

            ValueTask IAsyncDisposable.DisposeAsync() => default;
        }

        private sealed class Element
        {
            private readonly TaskCompletionSource<Element> next;
            private readonly object value;

            public Element(T value)
            {
                this.value = value;
                this.next = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public static Element CreateInitial() => new Element(
                AsyncEnumerable.InitialValue,
                new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously));

            public static Element CreateDisposed()
            {
                var tcs = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
                tcs.SetException(new ObjectDisposedException("This instance has been disposed"));
                return new Element(AsyncEnumerable.DisposedValue, tcs);
            }

            private Element(object value, TaskCompletionSource<Element> next)
            {
                this.value = value;
                this.next = next;
            }

            public bool IsValid => !this.IsInitial && !this.IsDisposed;

            public T Value
            {
                get
                {
                    if (this.IsInitial) ThrowInvalidInstance();
                    if (this.IsDisposed) ThrowDisposed();
                    if (this.value is T typedValue) return typedValue;
                    return default;
                }
            }

            public bool IsInitial => ReferenceEquals(this.value, AsyncEnumerable.InitialValue);
            public bool IsDisposed => ReferenceEquals(this.value, AsyncEnumerable.DisposedValue);

            public Task<Element> NextAsync() => this.next.Task;

            public void SetNext(Element next) => this.next.SetResult(next);

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static T ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");

            [MethodImpl(MethodImplOptions.NoInlining)]
            private static T ThrowDisposed() => throw new ObjectDisposedException("This instance has been disposed");
        }
    }

    /// <summary>
    /// Asynchronous version of the <see cref="System.Collections.Generic.IEnumerable{T}"/> interface, allowing elements of the enumerable sequence to be retrieved asynchronously.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    internal interface IAsyncEnumerable<out T>
    {
        /// <summary>
        /// Gets an asynchronous enumerator over the sequence.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token used to cancel the enumeration.</param>
        /// <returns>Enumerator for asynchronous enumeration over the sequence.</returns>
        IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Asynchronous version of the <see cref="System.Collections.Generic.IEnumerator{T}"/> interface, allowing elements to be retrieved asynchronously.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    internal interface IAsyncEnumerator<out T> : IAsyncDisposable
    {
        /// <summary>
        /// Gets the current element in the iteration.
        /// </summary>
        T Current { get; }

        /// <summary>
        /// Advances the enumerator to the next element in the sequence, returning the result asynchronously.
        /// </summary>
        /// <returns>
        /// Task containing the result of the operation: true if the enumerator was successfully advanced
        /// to the next element; false if the enumerator has passed the end of the sequence.
        /// </returns>
        ValueTask<bool> MoveNextAsync();
    }

    internal interface IAsyncDisposable
    {
        ValueTask DisposeAsync();
    }
}
