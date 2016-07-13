using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Orleans;
using Orleans.CodeGeneration;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams.AdHoc;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    using Orleans.Streams;

    public class ReactiveGrain<T> : Grain, IReactiveGrain<T>
    {
        public IAsyncObservable<T> GetStream(T[] values)
        {
            return Observable.Create<T>(async observer =>
            {
                if (values == null) throw new ArgumentNullException(nameof(values));
                try
                {
                    foreach (var value in values) await observer.OnNextAsync(value);
                    await observer.OnCompletedAsync();
                }
                catch (Exception exception)
                {
                    await observer.OnErrorAsync(exception);
                }

                return new SimpleStreamSubscriptionHandle<T>();
            });
        }

        public IAsyncObservable<string> JoinChatRoom(string room) => GrainFactory.GetGrain<IChatRoomGrain>(room).JoinRoom();
    }

    public class ChatRoomGrain : Grain, IChatRoomGrain, IGrainInvokeInterceptor
    {
        private readonly MulticastObservable<string> room = new MulticastObservable<string>();

        public IAsyncObservable<string> JoinRoom() => room;

        public Task SendMessage(string message) => room.OnNext(message);

        public Task<int> GetCurrentUserCount() => Task.FromResult(room.Count);

        private Logger log;

        public async Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            var msg = $"{method.Name}({string.Join(", ", request.Arguments ?? new object[] { })})";
            log.Info(msg);
            try
            {
                var result = await invoker.Invoke(this, request);
                log.Info(msg + " = " + (result ?? "null"));
                return result;
            }
            catch (Exception exception)
            {
                log.Warn(exception.GetHashCode(), msg + " failed", exception);
                throw;
            }
        }

        public override Task OnActivateAsync()
        {
            this.log = this.GetLogger(this.GetType().Name + "/" + this.GetPrimaryKeyString());
            return base.OnActivateAsync();
        }
    }

    [StorageProvider(ProviderName = "Default")]
    internal class ChatUserGrain : Grain<ChatUserGrainState>, IChatUserGrain, IGrainInvokeInterceptor
    {
        private Logger log;

        private Guid lifetimeId;

        private IAsyncObserver<string> GetObserver(ChatRoomMailbox room)
            => new BufferedObserver<string>(room.Messages) { OnNextDelegate = (_, __) => this.WriteStateAsync() };

        public async Task<object> Invoke(MethodInfo method, InvokeMethodRequest request, IGrainMethodInvoker invoker)
        {
            var msg = $"{method.Name}({string.Join(", ", request.Arguments ?? new object[] { })})";
            log.Info(msg);
            try
            {
                var result = await invoker.Invoke(this, request);
                log.Info(msg + " = " + (result ?? "null"));
                return result;
            }
            catch (Exception exception)
            {
                log.Warn(exception.GetHashCode(), msg + " failed", exception);
                throw;
            }
        }

        public Task<List<string>> MessagesSince(int id, string roomName)
        {
            ChatRoomMailbox room;
            if (this.State.Rooms.TryGetValue(roomName, out room))
            {
                return Task.FromResult(room.Messages.Skip(id).ToList());
            }

            return Task.FromResult(new List<string>());
        }

        public async Task JoinRoom(string roomName)
        {
            // Note that in a real transient chat room, leave and join should be combined into 'rejoin' so that the action
            // can be taken atomically to ensure no missed messages.
            await LeaveRoom(roomName);
            var roomGrain = GrainFactory.GetGrain<IChatRoomGrain>(roomName).JoinRoom();
            var room = new ChatRoomMailbox();
            room.Subscription = await roomGrain.SubscribeAsync(GetObserver(room));
            this.State.Rooms[roomName] = room;
            await this.WriteStateAsync();
        }

        public async Task LeaveRoom(string roomName)
        {
            ChatRoomMailbox room;
            if (this.State.Rooms.TryGetValue(roomName, out room))
            {
                await room.Subscription.UnsubscribeAsync();
            }
        }

        public Task Deactivate()
        {
            this.DeactivateOnIdle();
            return Task.FromResult(0);
        }

        public Task<Guid> GetLifetimeId() => Task.FromResult(this.lifetimeId);

        public override async Task OnActivateAsync()
        {
            this.lifetimeId = Guid.NewGuid();
            this.log = this.GetLogger(this.GetType().Name + "/" + this.GetPrimaryKeyString());

            // Renew existing subscriptions.
            if (this.State.Rooms.Count > 0)
            {
                foreach (var room in this.State.Rooms.Values)
                {
                    room.Subscription = await room.Subscription.ResumeAsync(GetObserver(room));
                }

                await this.WriteStateAsync();
            }

            await base.OnActivateAsync();
        }
    }

    public class MulticastObservable<T> : IAsyncObservable<T>
    {
        private readonly HashSet<IAsyncObserver<T>> observers = new HashSet<IAsyncObserver<T>>();

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
        {
            observers.Add(observer);
            return Task.FromResult<StreamSubscriptionHandle<T>>(new FuncAsyncDisposable<T>(() =>
            {
                observers.Remove(observer);
                return Task.FromResult(0);
            }));
        }

        public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
            IAsyncObserver<T> observer,
            StreamSequenceToken token,
            StreamFilterPredicate filterFunc = null,
            object filterData = null)
        {
            return this.SubscribeAsync(observer);
        }

        public async Task OnNext(T value)
        {
            List<IAsyncObserver<T>> toRemove = null;
            await Task.WhenAll(observers.Select(async observer =>
                                                      {
                                                          try
                                                          {
                                                              await observer.OnNextAsync(value);
                                                          }
                                                          catch
                                                          {
                                                              if (toRemove == null) toRemove = new List<IAsyncObserver<T>>();
                                                              toRemove.Add(observer);
                                                          }
                                                      }));
            toRemove?.ForEach(_ => observers.Remove(_));
        }

        public int Count => observers.Count;
    }

    public class SimpleStreamSubscriptionHandle<T> : StreamSubscriptionHandle<T>, IAsyncDisposable
    {
        private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>();

        public Task Disposed => completion.Task;

        public Task Dispose()
        {
            completion.TrySetResult(0);
            return Task.FromResult(0);
        }

        public bool IsDisposed => completion.Task.IsCompleted;

        public override IStreamIdentity StreamIdentity { get; }

#warning implement this!
        public override Guid HandleId { get; }

        public override Task UnsubscribeAsync() => this.Dispose();

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null)
        {
            throw new NotImplementedException();
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            throw new NotImplementedException();
        }
    }

    public class FuncAsyncDisposable<T> : StreamSubscriptionHandle<T>
    {
        private readonly Func<Task> onUnsubscribe;

        public FuncAsyncDisposable(Func<Task> onUnsubscribe)
        {
            this.onUnsubscribe = onUnsubscribe;
        }

        public Task Dispose() => this.UnsubscribeAsync();

#warning implement this!
        public override IStreamIdentity StreamIdentity { get; }

        public override Guid HandleId { get; }

        public override Task UnsubscribeAsync()
        {
            return this.onUnsubscribe();
        }

        public override Task<StreamSubscriptionHandle<T>> ResumeAsync(IAsyncObserver<T> observer, StreamSequenceToken token = null)
        {
            throw new NotImplementedException();
        }

        public override bool Equals(StreamSubscriptionHandle<T> other)
        {
            throw new NotImplementedException();
        }
    }

    public static class Observable
    {
        public static IAsyncObservable<T> Create<T>(Func<IAsyncObserver<T>, Task<StreamSubscriptionHandle<T>>> onSubscribe)
        {
            return new FuncObservable<T>(onSubscribe);
        }

        public static IAsyncObservable<T> Empty<T>()
        {
            return new EmptyObservable<T>();
        }

        public static IAsyncObservable<T> Return<T>(IEnumerable<T> values)
        {
            return new FuncObservable<T>(observer =>
            {
                var disposable = new SimpleStreamSubscriptionHandle<T>();
                Task.Factory.StartNew(async () =>
                {
                    foreach (var value in values)
                    {
                        if (disposable.IsDisposed) return;
                        await Task.WhenAny(observer.OnNextAsync(value), disposable.Disposed);
                    }

                    await Task.WhenAny(observer.OnCompletedAsync(), disposable.Disposed);
                });
                return Task.FromResult<StreamSubscriptionHandle<T>>(disposable);
            });
        }

        private class EmptyObservable<T> : IAsyncObservable<T>
        {
            public async Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
            {
                await observer.OnCompletedAsync();
                return new SimpleStreamSubscriptionHandle<T>();
            }

            public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
                IAsyncObserver<T> observer,
                StreamSequenceToken token,
                StreamFilterPredicate filterFunc = null,
                object filterData = null)
            {
                throw new NotImplementedException();
            }
        }

        private class FuncObservable<T> : IAsyncObservable<T>
        {
            [NonSerialized]
            private readonly Func<IAsyncObserver<T>, Task<StreamSubscriptionHandle<T>>> onSubscribe;

            public FuncObservable(Func<IAsyncObserver<T>, Task<StreamSubscriptionHandle<T>>> onSubscribe)
            {
                this.onSubscribe = onSubscribe;
            }

            public Task<StreamSubscriptionHandle<T>> SubscribeAsync(IAsyncObserver<T> observer)
            {
                return onSubscribe(observer);
            }

            public Task<StreamSubscriptionHandle<T>> SubscribeAsync(
                IAsyncObserver<T> observer,
                StreamSequenceToken token,
                StreamFilterPredicate filterFunc = null,
                object filterData = null)
            {
                return this.onSubscribe(observer);
            }
        }
    }
}