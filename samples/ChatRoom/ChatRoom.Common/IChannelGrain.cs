using System;
using System.Threading.Tasks;
using Orleans;

namespace ChatRoom
{
	public interface IChannelGrain : IGrainWithStringKey
	{
	    Task<Guid> Join(string nickname);
	    Task<Guid> Leave(string nickname);
	    Task<bool> Message(ChatMsg msg);
	    Task<ChatMsg[]> ReadHistory(int numberOfMessages);
	    Task<string[]> GetMembers();
	}
}
