using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Streams.AdHoc;

namespace UnitTests.GrainInterfaces
{
    public interface IReactiveGrain<T> : IGrainWithGuidKey
    {
        IGrainObservable<T> GetStream(T[] values);
        IGrainObservable<string> JoinChatRoom(string room);
    }

    public interface IChatRoomGrain : IGrainWithStringKey
    {
        IGrainObservable<string> JoinRoom();
        Task SendMessage(string message);
        Task<int> GetCurrentUserCount();
    }

    public interface IChatUserGrain : IGrainWithStringKey
    {
        Task<List<string>> MessagesSince(int id, string roomName);
        Task JoinRoom(string room);
        Task LeaveRoom(string room);
    }
    [Serializable]
    public class ChatUserGrainState
    {
        public Dictionary<string, ChatRoomMailbox> Rooms { get; } = new Dictionary<string, ChatRoomMailbox>();
    }

    [Serializable]
    public class ChatRoomMailbox
    {
        public IAsyncDisposable Subscription { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }

    public class BufferedObserver<T> : IGrainObserver<T>
    {
        private readonly TaskCompletionSource<T[]> values = new TaskCompletionSource<T[]>();
        private ConcurrentBag<Task> waiters;

        public BufferedObserver()
        {
            Buffer = new List<T>();
        }

        public BufferedObserver(List<T> buffer)
        {
            this.Buffer = buffer;
        }

        public Task OnNext(T value)
        {
            Buffer.Add(value);
            return Task.FromResult(0);
        }

        public Task OnError(Exception exception)
        {
            values.TrySetException(exception);
            return Task.FromResult(0);
        }

        public Task OnCompleted()
        {
            values.TrySetResult(Buffer.ToArray());
            return Task.FromResult(0);
        }

        public Task<T[]> WhenNext() => values.Task;

        public void Clear() => Buffer.Clear();
        
        public List<T> Buffer { get; }
    }
}
