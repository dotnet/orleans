using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal static class ChangeFeedSource
    {
        internal static readonly object InvalidValue = new object();
    }

    internal sealed class ChangeFeedSource<T>
    {
        private enum PublishResult
        {
            Success,
            InvalidUpdate,
            Failure
        }

        private readonly object updateLock = new object();
        private readonly Func<T, T, bool> updateValidator;
        private ChangeFeedNode current;

        public ChangeFeedSource(Func<T, T, bool> updateValidator)
        {
            this.updateValidator = updateValidator;
            this.current = ChangeFeedNode.CreateInitial();
        }

        public ChangeFeedSource(Func<T, T, bool> updateValidator, T initial)
        {
            this.updateValidator = updateValidator;
            this.current = new ChangeFeedNode(initial);
        }

        public ChangeFeedEntry<T> Current => this.current;

        public bool TryPublish(T value) => this.TryPublishInternal(value) == PublishResult.Success;

        private PublishResult TryPublishInternal(T value)
        {
            lock (this.updateLock)
            {
                if (this.current.HasValue && !this.updateValidator(this.current.Value, value))
                {
                    return PublishResult.InvalidUpdate;
                }

                var newItem = new ChangeFeedNode(value);
                if (this.current.TrySetNext(newItem))
                {
                    Interlocked.Exchange(ref this.current, newItem);
                    return PublishResult.Success;
                }

                return PublishResult.Failure;
            }
        }

        public void Publish(T value)
        {
            switch (this.TryPublishInternal(value))
            {
                case PublishResult.Success:
                    return;
                case PublishResult.Failure:
                    ThrowConcurrency();
                    break;
                case PublishResult.InvalidUpdate:
                    ThrowInvalidUpdate();
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidUpdate() => throw new ArgumentException("The value was not valid");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowConcurrency() => throw new InvalidOperationException("An update was concurrently published by another thread");

        private sealed class ChangeFeedNode : ChangeFeedEntry<T>
        {
            private readonly TaskCompletionSource<ChangeFeedEntry<T>> next;
            private readonly object value;

            public ChangeFeedNode(T value)
            {
                this.value = value;
                this.next = CreateCompletion();
            }

            public static ChangeFeedNode CreateInitial() => new ChangeFeedNode();

            private ChangeFeedNode()
            {
                this.value = ChangeFeedSource.InvalidValue;
                this.next = CreateCompletion();
            }

            public override bool HasValue => !ReferenceEquals(this.value, ChangeFeedSource.InvalidValue);

            public override T Value
            {
                get
                {
                    if (!this.HasValue) ThrowInvalidInstance();
                    if (this.value is T typedValue) return typedValue;
                    return default;
                }
            }

            public override Task<ChangeFeedEntry<T>> NextAsync() => this.next.Task;

            public bool TrySetNext(ChangeFeedNode next) => this.next.TrySetResult(next);

            private static T ThrowInvalidInstance() => throw new InvalidOperationException("This instance does not have a value set.");

            private static TaskCompletionSource<ChangeFeedEntry<T>> CreateCompletion()
                => new TaskCompletionSource<ChangeFeedEntry<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
