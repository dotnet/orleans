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
            current = new Element(initial);
        }

        public Action<T> OnPublished { get; set; }

        public bool TryPublish(T value) => TryPublish(new Element(value)) == PublishResult.Success;
        
        public void Publish(T value)
        {
            switch (TryPublish(new Element(value)))
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
            if (current.IsDisposed) return PublishResult.Disposed;

            lock (updateLock)
            {
                if (current.IsDisposed) return PublishResult.Disposed;

                if (current.IsValid && newItem.IsValid && !updateValidator(current.Value, newItem.Value))
                {
                    return PublishResult.InvalidUpdate;
                }

                var curr = current;
                Interlocked.Exchange(ref current, newItem);
                if (newItem.IsValid) OnPublished?.Invoke(newItem.Value);
                curr.SetNext(newItem);

                return PublishResult.Success;
            }
        }

        public void Dispose()
        {
            if (current.IsDisposed) return;

            lock (updateLock)
            {
                if (current.IsDisposed) return;

                TryPublish(Element.CreateDisposed());
            }
        }

        private void ThrowInvalidUpdate() => throw new ArgumentException("The value was not valid");

        private void ThrowDisposed() => throw new ObjectDisposedException("This instance has been disposed");

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new AsyncEnumerator(current, cancellationToken);
        }

        private sealed class AsyncEnumerator : IAsyncEnumerator<T>
        {
            private readonly Task cancellation;
            private Element current;

            public AsyncEnumerator(Element initial, CancellationToken cancellation)
            {
                if (!initial.IsValid) current = initial;
                else
                {
                    var result = Element.CreateInitial();
                    result.SetNext(initial);
                    current = result;
                }

                if (cancellation != default)
                {
                    this.cancellation = cancellation.WhenCancelled();
                }
            }

            T IAsyncEnumerator<T>.Current => current.Value;

            async ValueTask<bool> IAsyncEnumerator<T>.MoveNextAsync()
            {
                Task<Element> next;
                if (cancellation != default)
                {
                    next = current.NextAsync();
                    var result = await Task.WhenAny(cancellation, next);
                    if (ReferenceEquals(result, cancellation)) return false;
                }
                else
                {
                    next = current.NextAsync();
                }

                current = await next;
                return current.IsValid;
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
                next = new TaskCompletionSource<Element>(TaskCreationOptions.RunContinuationsAsynchronously);
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

            public bool IsValid => !IsInitial && !IsDisposed;

            public T Value
            {
                get
                {
                    if (IsInitial) ThrowInvalidInstance();
                    ObjectDisposedException.ThrowIf(IsDisposed, this);
                    if (value is T typedValue) return typedValue;
                    return default;
                }
            }

            public bool IsInitial => ReferenceEquals(value, AsyncEnumerable.InitialValue);
            public bool IsDisposed => ReferenceEquals(value, AsyncEnumerable.DisposedValue);

            public Task<Element> NextAsync() => next.Task;

            public void SetNext(Element next) => this.next.SetResult(next);

            private void ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");
        }
    }
}
