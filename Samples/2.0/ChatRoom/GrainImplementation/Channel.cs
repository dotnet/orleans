using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GrainInterfaces;
using GrainInterfaces.Model;
using Orleans;
using Orleans.Streams;
using Utils;

namespace GrainImplementation
{
	public class Channel : Grain, IChannel
	{
		private readonly List<ChatMsg> messages = new List<ChatMsg>(100);
		private readonly List<string> onlineMembers = new List<string>(10);

		private IAsyncStream<ChatMsg> stream;

		public override Task OnActivateAsync()
		{
			var streamProvider = GetStreamProvider(Constants.ChatRoomStreamProvider);

		    stream = streamProvider.GetStream<ChatMsg>(Guid.NewGuid(), Constants.CharRoomStreamNameSpace);
            return base.OnActivateAsync();
		}

		public async Task<Guid> Join(string nickname)
		{
			onlineMembers.Add(nickname);

			await stream.OnNextAsync(new ChatMsg("System", $"{nickname} joins the chat '{this.GetPrimaryKeyString()}' ..."));

			return stream.Guid;
		}

		public async Task<Guid> Leave(string nickname)
		{
			onlineMembers.Remove(nickname);
			await stream.OnNextAsync(new ChatMsg("System", $"{nickname} leaves the chat..."));

			return stream.Guid;
		}

		public async Task<bool> Message(ChatMsg msg)
		{
			messages.Add(msg);
			await stream.OnNextAsync(msg);

			return true;
		}

	    public Task<string[]> GetMembers()
	    {
	        return Task.FromResult(onlineMembers.ToArray());
	    }

	    public Task<ChatMsg[]> ReadHistory(int numberOfMessages)
	    {
	        var response = messages
	            .OrderByDescending(x => x.Created)
	            .Take(numberOfMessages)
	            .OrderBy(x => x.Created)
	            .ToArray();

	        return Task.FromResult(response);
	    }
    }
}