using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace UnitTests.GrainInterfaces
{
    using Orleans.Streams;

    public interface IReactiveGrain<T> : IGrainWithGuidKey
    {
        IAsyncObservable<T> GetStream(T[] values);
        IAsyncObservable<string> JoinChatRoom(string room);
        IAsyncObservable<T> Error(string message);
        IAsyncObservable<T> ThrowOnSubscribe(string message);
        IAsyncObservable<T> ThrowImmediately(string message);
        IAsyncObservable<T> Empty();
    }

    public interface IChatRoomGrain : IGrainWithStringKey
    {
        IAsyncObservable<string> JoinRoom();
        Task SendMessage(string message);
        Task<int> GetCurrentUserCount();
    }
    
    public interface IChatUserGrain : IGrainWithStringKey
    {
        Task<List<string>> MessagesSince(int id, string roomName);
        Task JoinRoom(string room);
        Task LeaveRoom(string room);
        Task Deactivate();
        Task<Guid> GetLifetimeId();
    }

    [Serializable]
    public class ChatUserGrainState
    {
        public Dictionary<string, ChatRoomMailbox> Rooms { get; } = new Dictionary<string, ChatRoomMailbox>();
    }

    [Serializable]
    public class ChatRoomMailbox
    {
        public StreamSubscriptionHandle<string> Subscription { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }

    public class BufferedObserver<T> : IAsyncObserver<T>
    {
        public BufferedObserver()
        {
            this.Buffer = new List<T>();
        }

        public BufferedObserver(List<T> buffer)
        {
            this.Buffer = buffer;
        }

        public Func<T, StreamSequenceToken, Task> OnNextDelegate { get; set; } = (_, __) => Task.FromResult(0);
        public Func<Exception, Task> OnErrorDelegate { get; set; } = _ => Task.FromResult(0);
        public Func<Task> OnCompletedDelegate { get; set; } = () => Task.FromResult(0);

        public Task OnNextAsync(T value, StreamSequenceToken token = null)
        {
            this.Buffer.Add(value);
            return OnNextDelegate(value, token);
        }

        public Task OnErrorAsync(Exception exception)
        {
            return OnErrorDelegate(exception);
        }

        public Task OnCompletedAsync()
        {
            return OnCompletedDelegate();
        }
        
        public List<T> Buffer { get; }
    }
}
