#nullable enable
using System;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace Orleans.Runtime.Messaging;

internal sealed class MessageWriteRequest : WriteRequest
{
    private readonly MessageHandlerShared _shared;
    private readonly ArcBufferWriter _buffer = new();
    private readonly List<(int TotalLength, int HeaderLength)> _messageSizes = [];
    private Connection? _connection;
    private MessageSerializer? _messageSerializer;

    public MessageWriteRequest(MessageHandlerShared shared)
    {
        _shared = shared;
        Buffers = new(_buffer);
    }

    public List<Message> Messages { get; } = [];
    public int Length => _buffer.Length;
    internal override bool HasLargeMessages
        => _messageSizes.Exists(static size => size.TotalLength >= 8 * 1024);

    public void Initialize(Connection connection) => _connection = connection;

    public void WriteMessage(Message message)
    {
        var startLength = _buffer.Length;
        var messageSerializer = _messageSerializer ??= _shared.GetMessageSerializer();
        try
        {
            // Reserve space for framing
            var framingBytes = _buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            _buffer.AdvanceWriter(Message.LENGTH_HEADER_SIZE);

            // Serialize the message in full
            var (headerLength, bodyLength) = messageSerializer.Write(_buffer, message);

            // Write the framing
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes[sizeof(int)..], bodyLength);

            Messages.Add(message);
            _messageSizes.Add((headerLength + bodyLength, headerLength));
        }
        catch
        {
            _buffer.Truncate(startLength);
            throw;
        }
    }

    public void CompleteWriting()
    {
        if (_messageSerializer is { } serializer)
        {
            _messageSerializer = null;
            _shared.Return(serializer);
        }
    }

    public override void SetResult()
    {
        try
        {
            var connection = _connection ?? throw new InvalidOperationException("The write request has no owning connection.");
            for (var i = 0; i < Messages.Count; i++)
            {
                var (totalLength, headerLength) = _messageSizes[i];
                connection.RecordMessageSend(Messages[i], totalLength, headerLength);
            }
        }
        finally
        {
            foreach (var message in Messages)
            {
                message.ReleaseBodyBuffer();
            }

            Reset();
        }
    }

    public override void SetException(Exception error)
    {
        _shared.ConnectionTrace.LogError(error, "Error sending messages {Messages}", Messages);
        var connection = _connection ?? throw new InvalidOperationException("The write request has no owning connection.");
        foreach (var message in Messages)
        {
            connection.RerouteMessage(message, error);
        }

        Reset();
    }

    public void Reset()
    {
        CompleteWriting();
        Messages.Clear();
        _messageSizes.Clear();
        _buffer.Reset();
        _connection = null;
        _shared.Return(this);
    }
}
