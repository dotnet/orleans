using System;
using System.Threading.Tasks;
using GrainInterfaces.Model;
using Orleans;

namespace GrainInterfaces
{
	public interface IChannel : IGrainWithStringKey
	{
	    Task<Guid> Join(string nickname);
	    Task<Guid> Leave(string nickname);
	    Task<bool> Message(ChatMsg msg);
	    Task<ChatMsg[]> ReadHistory(int numberOfMessages);
	    Task<string[]> GetMembers();
	}
}
