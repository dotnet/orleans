using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans;
using Orleans.Providers;
using Orleans.Streams.AdHoc;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ReactiveGrain<T> : Grain, IReactiveGrain<T>
    {
        public IGrainObservable<T> GetStream(T[] values)
        {
            return Observable.Create<T>(async observer =>
            {
                if (values == null) throw new ArgumentNullException(nameof(values));
                try
                {
                    foreach (var value in values) await observer.OnNext(value);
                    await observer.OnCompleted();
                }
                catch (Exception exception)
                {
                    await observer.OnError(exception);
                }

                return new SimpleAsyncDisposable();
            });
        }

        public IGrainObservable<string> JoinChatRoom(string room)
            => GrainFactory.GetGrain<IChatRoomGrain>(room).JoinRoom();
    }

    public class ChatRoomGrain : Grain, IChatRoomGrain
    {
        private readonly MulticastObservable<string> room = new MulticastObservable<string>();

        public IGrainObservable<string> JoinRoom() => room;

        public Task SendMessage(string message) => room.OnNext(message);

        public Task<int> GetCurrentUserCount() => Task.FromResult(room.Count);
    }

    [StorageProvider(ProviderName = "Default")]
    internal class ChatUserGrain : Grain<ChatUserGrainState>, IChatUserGrain
    {
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
            room.Subscription = await roomGrain.Subscribe(new BufferedObserver<string>(room.Messages));
            this.State.Rooms[roomName] = room;
            await this.WriteStateAsync();
        }

        public async Task LeaveRoom(string roomName)
        {
            ChatRoomMailbox room;
            if (this.State.Rooms.TryGetValue(roomName, out room))
            {
                await room.Subscription.Dispose();
            }
        }

        public override async Task OnActivateAsync()
        {
            // Join all the rooms which were previously joined.
            foreach (var room in this.State.Rooms.Keys.ToList())
            {
                await JoinRoom(room);
            }

            await base.OnActivateAsync();
        }
    }

    public class MulticastObservable<T> : IGrainObservable<T>
    {
        private readonly HashSet<IGrainObserver<T>> observers = new HashSet<IGrainObserver<T>>();

        public Task<IAsyncDisposable> Subscribe(IGrainObserver<T> observer)
        {
            observers.Add(observer);
            return Task.FromResult<IAsyncDisposable>(new FuncAsyncDisposable(() =>
            {
                observers.Remove(observer);
                return Task.FromResult(0);
            }));
        }

        public async Task OnNext(T value)
        {
            var tasks = new List<Task>(observers.Count);
            List<IGrainObserver<T>> toRemove = null;
            tasks.AddRange(observers.Select(async observer =>
            {
                try
                {
                    await observer.OnNext(value);
                }
                catch
                {
                    if (toRemove == null) toRemove = new List<IGrainObserver<T>>();
                    toRemove.Add(observer);
                }
            }));

            await Task.WhenAll(tasks);
            toRemove?.ForEach(_ => observers.Remove(_));
        }

        public int Count => observers.Count;
    }

    public class SimpleAsyncDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource<int> completion = new TaskCompletionSource<int>();

        public Task Disposed => completion.Task;
        public Task Dispose()
        {
            completion.TrySetResult(0);
            return Task.FromResult(0);
        }

        public bool IsDisposed => completion.Task.IsCompleted;
    }

    public class FuncAsyncDisposable : IAsyncDisposable
    {
        private readonly Func<Task> onDispose;

        public FuncAsyncDisposable(Func<Task> onDispose)
        {
            this.onDispose = onDispose;
        }

        public Task Dispose() => onDispose();
    }

    public static class Observable
    {
        public static IGrainObservable<T> Create<T>(Func<IGrainObserver<T>, Task<IAsyncDisposable>> onSubscribe)
        {
            return new FuncObservable<T>(onSubscribe);
        }

        public static IGrainObservable<T> Empty<T>()
        {
            return new EmptyObservable<T>();
        }

        public static IGrainObservable<T> Return<T>(IEnumerable<T> values)
        {
            return new FuncObservable<T>(observer =>
            {
                var disposable = new SimpleAsyncDisposable();
                Task.Factory.StartNew(async () =>
                {
                    foreach (var value in values)
                    {
                        if (disposable.IsDisposed) return;
                        await Task.WhenAny(observer.OnNext(value), disposable.Disposed);
                    }

                    await Task.WhenAny(observer.OnCompleted(), disposable.Disposed);
                });
                return Task.FromResult<IAsyncDisposable>(disposable);
            });
        }

        private class EmptyObservable<T> : IGrainObservable<T>
        {
            public async Task<IAsyncDisposable> Subscribe(IGrainObserver<T> observer)
            {
                await observer.OnCompleted();
                return new SimpleAsyncDisposable();
            }
        }

        private class FuncObservable<T> : IGrainObservable<T>
        {
            [NonSerialized]
            private readonly Func<IGrainObserver<T>, Task<IAsyncDisposable>> onSubscribe;

            public FuncObservable(Func<IGrainObserver<T>, Task<IAsyncDisposable>> onSubscribe)
            {
                this.onSubscribe = onSubscribe;
            }

            public Task<IAsyncDisposable> Subscribe(IGrainObserver<T> observer)
            {
                return onSubscribe(observer);
            }
        }
    }
}
