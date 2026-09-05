#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Diagnostics;

namespace Orleans.Runtime.Messaging;

internal sealed partial class MessageReadRequest(MessageHandlerShared shared) : ReadRequest, IThreadPoolWorkItem, IDisposable
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "MessageHandlerShared owns and pools this request; the request does not own the shared pool.")]
    internal readonly MessageHandlerShared Shared = shared;

    private Connection? _connection;
    private int _headerLength;
    private int _bodyLength;
    internal ArcBuffer _headers;
    private ArcBuffer _body;

    public int PayloadLength => _headerLength + _bodyLength;

    internal Message.ResponseTypes _originalResponseType;
    public ref ArcBuffer Headers => ref _headers;
    public ref ArcBuffer Body => ref _body;
    public int HeaderLength => _headerLength;
    public int BodyLength => _bodyLength;

    public void SetConnection(Connection connection)
    {
        Debug.Assert(_connection is null);
        _connection = connection;
    }

    public void Reset()
    {
        _headerLength = default;
        _bodyLength = default;
        _originalResponseType = default;
        _connection = default;
        _headers.Dispose();
        _body.Dispose();
        _headers = default;
        _body = default;
        Shared.Return(this);
    }

    internal void ReleaseHeaders()
    {
        _headers.Dispose();
        _headers = default;
    }

    public override void OnError(Exception error)
    {
        var connection = _connection ?? throw new InvalidOperationException("Cannot report read failure before a connection is set.");
        Reset();
        connection.OnReadCompleted(error);
    }

    public override void OnCanceled()
    {
        OnError(new OperationCanceledException());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The serializer is borrowed from and returned to MessageHandlerShared.")]
    public override bool OnRead(ArcBufferReader bufferReader)
    {
        Debug.Assert(_connection is not null);

        if (bufferReader.Length < Message.LENGTH_HEADER_SIZE)
        {
            return false;
        }

        if (_headerLength == 0 && _bodyLength == 0)
        {
            Span<byte> scratch = stackalloc byte[Message.LENGTH_HEADER_SIZE];
            var lengthBytes = bufferReader.Peek(in scratch);
            _headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            _bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes[sizeof(int)..]);
            var messageSerializer = Shared.GetMessageSerializer();
            try
            {
                messageSerializer.ValidateFrameLengths(_headerLength, _bodyLength);
            }
            finally
            {
                Shared.Return(messageSerializer);
            }

            bufferReader.Skip(Message.LENGTH_HEADER_SIZE);
        }

        if (bufferReader.Length < PayloadLength)
        {
            return false;
        }

        _headers = bufferReader.ConsumeSlice(_headerLength);
        _body = bufferReader.ConsumeSlice(_bodyLength);
        Debug.Assert(_headers.Length == _headerLength);
        Debug.Assert(_body.Length == _bodyLength);

        _connection.EnqueueRead();
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        return true;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The decoded message is transferred to the receiver; failure paths dispose it or transfer it to an error response.")]
    void IThreadPoolWorkItem.Execute()
    {
        Message? message = null;
        var connection = _connection ?? throw new InvalidOperationException("Cannot process a message before a connection is set.");
        var shouldReset = true;
        MessageSerializer? messageSerializer = null;
        try
        {
            messageSerializer = Shared.GetMessageSerializer();
            messageSerializer.ReadHeaders(this, out message);
            message.MessageReceiver = connection;
            connection.MarkMessageReceived();
            connection.RecordMessageReceive(message, PayloadLength, HeaderLength);

            // Body deserialization is more likely to fail than header deserialization.
            // Separating the two allows for these kinds of errors to be propagated back to the caller.
            if (_bodyLength > 0)
            {
                // This instance is owned by the message now, so it will not be reset immediately.
                message.SetMessageReadRequest(this);
                shouldReset = false;
            }
            connection.OnReceivedMessage(message);
        }
        catch (Exception exception)
        {
            try
            {
                HandleReceiveMessageFailure(message, exception);
            }
            catch (Exception fatalException)
            {
                if (!shouldReset)
                {
                    message?.Dispose();
                }

                connection.OnReadCompleted(new AggregateException(exception, fatalException));
            }
        }
        finally
        {
            if (shouldReset)
            {
                Reset();
            }

            if (messageSerializer is not null)
            {
                Shared.Return(messageSerializer);
            }
        }

        void HandleReceiveMessageFailure(Message? message, Exception exception)
        {
            if (message is null)
            {
                LogExceptionReadingConnection(Shared.ConnectionTrace, exception, connection);

                connection.OnReadCompleted(exception);
                return;
            }

            LogExceptionReadingMessage(Shared.ConnectionTrace, exception, message, connection);

            // The message body was not successfully decoded, but the headers were.
            Shared.MessagingInstruments.OnRejectedMessage(message);

            if (message.HasDirection)
            {
                if (message.Direction == Message.Directions.Request)
                {
                    // Send a fast fail to the caller.
                    var response = Shared.MessageFactory.CreateResponseMessage(message);
                    response.Result = Message.ResponseTypes.Error;
                    response.BodyObject = Response.FromException(exception);

                    // Send the error response and continue processing the next message.
                    connection.Send(response);
                    message.Dispose();
                }
                else if (message.Direction == Message.Directions.Response)
                {
                    // If the message was a response, propagate the exception to the intended recipient.
                    message.Result = Message.ResponseTypes.Error;
                    message.BodyObject = Response.FromException(exception);
                    Shared.MessageCenter.DispatchLocalMessage(message);
                }
                else
                {
                    message.Dispose();
                }
            }
            else
            {
                message.Dispose();
            }

        }
    }
    public void Dispose() => Reset();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception reading message from connection {Connection}")]
    private static partial void LogExceptionReadingConnection(ILogger logger, Exception exception, Connection connection);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exception reading message {Message} from connection {Connection}")]
    private static partial void LogExceptionReadingMessage(ILogger logger, Exception exception, Message message, Connection connection);
}
