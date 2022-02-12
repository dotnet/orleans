using Orleans;
using Orleans.Streams;

namespace ChatRoom;

public class ChannelGrain : Grain, IChannelGrain
{
    private readonly List<ChatMsg> _messages = new(100);
    private readonly List<string> _onlineMembers = new(10);

    private IAsyncStream<ChatMsg> _stream = null!;

    public override Task OnActivateAsync()
    {
        var streamProvider = GetStreamProvider("chat");

        _stream = streamProvider.GetStream<ChatMsg>(
            Guid.NewGuid(), "default");

        return base.OnActivateAsync();
    }

    public async Task<Guid> Join(string nickname)
    {
        _onlineMembers.Add(nickname);

        await _stream.OnNextAsync(
            new ChatMsg(
                "System",
                $"{nickname} joins the chat '{this.GetPrimaryKeyString()}' ..."));

        return _stream.Guid;
    }

    public async Task<Guid> Leave(string nickname)
    {
        _onlineMembers.Remove(nickname);

        await _stream.OnNextAsync(
            new ChatMsg(
                "System",
                $"{nickname} leaves the chat..."));

        return _stream.Guid;
    }

    public async Task<bool> Message(ChatMsg msg)
    {
        _messages.Add(msg);

        await _stream.OnNextAsync(msg);

        return true;
    }

    public Task<string[]> GetMembers() => Task.FromResult(_onlineMembers.ToArray());

    public Task<ChatMsg[]> ReadHistory(int numberOfMessages)
    {
        var response = _messages
            .OrderByDescending(x => x.Created)
            .Take(numberOfMessages)
            .OrderBy(x => x.Created)
            .ToArray();

        return Task.FromResult(response);
    }
}
