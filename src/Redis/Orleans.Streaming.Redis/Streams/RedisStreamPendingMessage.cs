using Orleans.Streams;
using StackExchange.Redis;

namespace Orleans.Streaming.Redis.Streams;

internal record RedisStreamPendingMessage
{
    public RedisStreamPendingMessage(StreamEntry streamEntry, StreamSequenceToken token)
    {
        Token = token;
        StreamEntry = streamEntry;
    }

    public StreamEntry StreamEntry { get; }

    public StreamSequenceToken Token { get; }
}
