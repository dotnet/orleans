#nullable enable
using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Invocation;
using Orleans.Serialization.Buffers;
using System.Buffers.Binary;
using Orleans.Connections.Transport;
using System.Diagnostics;

namespace Orleans.Runtime.Messaging
{
    internal sealed class MessageReadRequest(MessageHandlerShared shared) : ReadRequest, IThreadPoolWorkItem, IDisposable
    {
        internal readonly MessageHandlerShared Shared = shared;

        private Connection? _connection;
        private int _headerLength;
        private int _bodyLength;
        internal ArcBuffer _headers;
        private ArcBuffer _body;

        public int FramedLength => Message.LENGTH_HEADER_SIZE + PayloadLength;
        public int PayloadLength => _headerLength + _bodyLength;

        internal Message.PackedHeaders _originalHeaders;
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
            Debug.Assert(_connection is not null);
            _headerLength = default;
            _bodyLength = default;
            _connection = default;
            _headers.Dispose();
            _body.Dispose();
            _headers = default;
            _body = default;
            Shared.Return(this);
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

        public override bool OnRead(ArcBufferReader bufferReader)
        {
            Debug.Assert(_connection is not null);

            if (bufferReader.Length < Message.LENGTH_HEADER_SIZE)
            {
                return false;
            }

            if (_headerLength == 0)
            {
                Span<byte> scratch = stackalloc byte[Message.LENGTH_HEADER_SIZE];
                var lengthBytes = bufferReader.Peek(in scratch);
                _headerLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
                _bodyLength = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes[sizeof(int)..]);
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
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
            return true;
        }

        void IThreadPoolWorkItem.Execute()
        {
            Message? message = null;
            var connection = _connection ?? throw new InvalidOperationException("Cannot process a message before a connection is set.");
            var shouldReset = true;
            var messageSerializer = Shared.GetMessageSerializer();
            try
            {
                messageSerializer.ReadHeaders(this, out message);

                // Body deserialization is more likely to fail than header deserialization.
                // Separating the two allows for these kinds of errors to be propagated back to the caller.
                if (_bodyLength > 0)
                {
                    // This instance is owned by the message now, so it will not be reset immediately.
                    message.SetMessageReadRequest(this);
                    shouldReset = false;
                }
                else
                {
                    // Otherwise, return this instance to the pool on exiting this method.
                }

                connection.OnReceivedMessage(message);
            }
            catch (Exception exception)
            {
                if (!HandleReceiveMessageFailure(message, exception))
                {
                    throw;
                }
            }
            finally
            {
                if (shouldReset)
                {
                    Reset();
                }

                Shared.Return(messageSerializer);
            }

            bool HandleReceiveMessageFailure(Message? message, Exception exception)
            {
                // If deserialization completely failed, rethrow the exception so that it can be handled at another level.
                if (message is null)
                {
                    Shared.ConnectionTrace.LogWarning(
                        exception,
                        "Exception reading message from connection {Connection}",
                        connection);

                    // Returning false here informs the caller that the exception should not be caught.
                    return false;
                }

                Shared.ConnectionTrace.LogWarning(
                    exception,
                    "Exception reading message {Message} from connection {Connection}",
                    message,
                    connection);

                // The message body was not successfully decoded, but the headers were.
                MessagingInstruments.OnRejectedMessage(message);

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
                    }
                    else if (message.Direction == Message.Directions.Response)
                    {
                        // If the message was a response, propagate the exception to the intended recipient.
                        message.Result = Message.ResponseTypes.Error;
                        message.BodyObject = Response.FromException(exception);
                        Shared.MessageCenter.DispatchLocalMessage(message);
                    }
                }

                // The exception has been handled by propagating it onwards.
                return true;
            }
        }

        public void Dispose() => Reset();
    }
}
