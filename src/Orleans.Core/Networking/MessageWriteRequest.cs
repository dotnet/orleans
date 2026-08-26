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
    private const int LargeMessageSize = 8 * 1024;
    private const int SendPageSize = 32 * 1024;
    private readonly MessageHandlerShared _shared;
    private readonly ArcBufferWriter _buffer = new();
    private readonly List<(Message Message, int TotalLength, int HeaderLength)> _messages = [];
    private Connection? _connection;
    private MessageSerializer? _messageSerializer;
    private bool _hasLargeMessages;

    public MessageWriteRequest(MessageHandlerShared shared)
    {
        _shared = shared;
        Buffers = new(_buffer);
    }

    public int MessageCount => _messages.Count;
    public int Length => _buffer.Length;
    internal override bool HasLargeMessages => _hasLargeMessages;

    public void Initialize(Connection connection) => _connection = connection;
    public Message GetMessage(int index) => _messages[index].Message;

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

            var totalLength = headerLength + bodyLength;
            _messages.Add((message, totalLength, headerLength));
            _hasLargeMessages |= totalLength >= LargeMessageSize;
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
            foreach (var (message, totalLength, headerLength) in _messages)
            {
                connection.RecordMessageSend(message, totalLength, headerLength);
            }
        }
        finally
        {
            foreach (var (message, _, _) in _messages)
            {
                message.ReleaseBodyBuffer();
            }

            Reset();
        }
    }

    public override void SetException(Exception error)
    {
        _shared.ConnectionTrace.LogError(error, "Error sending messages {Messages}", _messages);
        var connection = _connection ?? throw new InvalidOperationException("The write request has no owning connection.");
        foreach (var (message, _, _) in _messages)
        {
            connection.RerouteMessage(message, error);
        }

        Reset();
    }

    public void Reset()
    {
        var nextPageSize = _messages.Count == 1
            && _messages[0].TotalLength is >= LargeMessageSize and < SendPageSize
                ? SendPageSize
                : 0;
        CompleteWriting();
        _messages.Clear();
        _hasLargeMessages = false;
        _buffer.Reset(nextPageSize);
        _connection = null;
        _shared.Return(this);
    }
}
