using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Internal;

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

        private static void ThrowInvalidUpdate() => throw new ArgumentException("The value was not valid");

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

            private static T ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");
        }
    }
}
