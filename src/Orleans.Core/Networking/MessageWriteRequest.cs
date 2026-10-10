using System;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using Orleans.Internal;

namespace Orleans.Runtime.Messaging;

internal sealed partial class MessageWriteRequest : WriteRequest, IDisposable
{
    private const int LargeMessageSize = 8 * 1024;
    private const int SendPageSize = 32 * 1024;
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "MessageHandlerShared owns and pools this request; the request does not own the shared pool.")]
    private readonly MessageHandlerShared _shared;
    private readonly ArcBufferWriter _buffer = new();
    private readonly List<(Message Message, int TotalLength, int HeaderLength, AdmissionGate.Admission Admission)> _messages = [];
    private Connection? _connection;
    private MessageSerializer? _messageSerializer;
    private bool _hasLargeMessages;
    private bool _disposed;

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

    // Successful serialization transfers admission from the send queue to this write.
    public void WriteMessage(Message message, AdmissionGate.Admission admission)
    {
        var startLength = _buffer.Length;
        try
        {
            var messageSerializer = _messageSerializer ??= _shared.GetMessageSerializer();
            // Reserve space for framing
            var framingBytes = _buffer.GetSpan(Message.LENGTH_HEADER_SIZE);
            _buffer.AdvanceWriter(Message.LENGTH_HEADER_SIZE);

            // Serialize the message in full
            var (headerLength, bodyLength) = messageSerializer.Write(_buffer, message);

            // Write the framing
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes, headerLength);
            BinaryPrimitives.WriteInt32LittleEndian(framingBytes[sizeof(int)..], bodyLength);

            var totalLength = headerLength + bodyLength;
            _messages.Add((message, totalLength, headerLength, admission));
            _hasLargeMessages |= totalLength >= LargeMessageSize;
        }
        catch
        {
            _buffer.Truncate(startLength);
            if (message.RequiresApplicationDrain)
            {
                _connection?.OnApplicationWriteFailure(message);
            }
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
            foreach (var (message, totalLength, headerLength, _) in _messages)
            {
                connection.RecordMessageSend(message, totalLength, headerLength);
            }
        }
        finally
        {
            foreach (var (message, _, _, _) in _messages)
            {
                message.ReleaseBodyBuffer();
            }

            Reset();
        }
    }

    public override void SetException(Exception error)
    {
        if (error is ConnectionClosedException)
        {
            LogInformationConnectionClosedWhileSendingMessages(_shared.ConnectionTrace, error, _messages);
        }
        else
        {
            LogErrorSendingMessages(_shared.ConnectionTrace, error, _messages);
        }

        var connection = _connection ?? throw new InvalidOperationException("The write request has no owning connection.");
        for (var i = 0; i < _messages.Count; i++)
        {
            var message = _messages[i].Message;
            if (message.IsRelocatableRequest)
            {
                connection.OnApplicationWriteFailure(message);
                // An accepted write can have reached the receiver. The original callback awaits its outcome.
                _shared.MessagingInstruments.OnFailedSentMessage(message);
                message.Dispose();
            }
            else
            {
                RerouteMessage(i, error);
            }
        }

        Reset();
    }

    internal void RerouteMessage(int index, Exception? error = null)
    {
        var connection = _connection ?? throw new InvalidOperationException("The write request has no owning connection.");
        var (message, totalLength, headerLength, admission) = _messages[index];
        if (message.RequiresApplicationDrain)
        {
            connection.OnApplicationWriteFailure(message);
        }

        _messages[index] = (message, totalLength, headerLength, default);
        connection.RerouteMessage(message, error, admission);
    }

    public void Reset()
    {
        var nextPageSize = _messages.Count == 1
            && _messages[0].TotalLength is >= LargeMessageSize and < SendPageSize
                ? SendPageSize
                : 0;
        CompleteWriting();
        foreach (var (_, _, _, admission) in _messages)
        {
            admission.Dispose();
        }

        _messages.Clear();
        _hasLargeMessages = false;
        _buffer.Reset(nextPageSize);
        _connection = null;
        _shared.Return(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CompleteWriting();
        foreach (var (_, _, _, admission) in _messages)
        {
            admission.Dispose();
        }

        _messages.Clear();
        _buffer.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error sending messages {Messages}")]
    private static partial void LogErrorSendingMessages(ILogger logger, Exception error, object messages);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection closed while sending messages {Messages}")]
    private static partial void LogInformationConnectionClosedWhileSendingMessages(ILogger logger, Exception error, object messages);
}
