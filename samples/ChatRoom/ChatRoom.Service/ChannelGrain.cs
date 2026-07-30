using Orleans.Streams;

namespace ChatRoom;

public sealed class ChannelGrain : Grain, IChannelGrain
{
    private readonly List<ChatMsg> _messages = [];
    private readonly List<string> _onlineMembers = [];

    // Initialized in OnActivateAsync that runs before
    // other methods that uses _stream field can be invoked.
    private IAsyncStream<ChatMsg> _stream = default!;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider("chat");

        var streamId = StreamId.Create("ChatRoom", this.GetPrimaryKeyString());

        _stream = streamProvider.GetStream<ChatMsg>(streamId);

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<StreamId> Join(string nickname)
    {
        _onlineMembers.Add(nickname);

        await _stream.OnNextAsync(
            new ChatMsg(
                Author: "System",
                Text: $"{nickname} joins the chat '{this.GetPrimaryKeyString()}' ..."));

        return _stream.StreamId;
    }

    public async Task<StreamId> Leave(string nickname)
    {
        _onlineMembers.Remove(nickname);

        await _stream.OnNextAsync(
            new ChatMsg(
                Author: "System",
                Text: $"{nickname} leaves the chat..."));

        return _stream.StreamId;
    }

    public async Task<bool> Message(ChatMsg msg)
    {
        _messages.Add(msg);

        if (_messages.Count > 100)
        {
            _messages.RemoveAt(0);
        }

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
