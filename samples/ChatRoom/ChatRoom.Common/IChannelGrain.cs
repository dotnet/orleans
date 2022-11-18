using Orleans.Runtime;

namespace ChatRoom;

public interface IChannelGrain : IGrainWithStringKey
{
    Task<StreamId> Join(string nickname);
    Task<StreamId> Leave(string nickname);
    Task<bool> Message(ChatMsg msg);
    Task<ChatMsg[]> ReadHistory(int numberOfMessages);
    Task<string[]> GetMembers();
}
