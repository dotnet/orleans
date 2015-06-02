/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using Orleans.Runtime;
 
namespace Orleans.Messaging
{
    /// <summary>
    /// The Receiver class is used by the GatewayConnection to receive messages. It runs its own thread, but it performs all i/o operations synchronously.
    /// </summary>
    internal class GatewayClientReceiver : AsynchAgent
    {
        private readonly GatewayConnection gatewayConnection;
        private readonly IncomingMessageBuffer buffer;

        internal GatewayClientReceiver(GatewayConnection gateway)
        {
            gatewayConnection = gateway;
            OnFault = FaultBehavior.RestartOnFault;
            buffer = new IncomingMessageBuffer(Log, true); 
        }

        protected override void Run()
        {
            if (gatewayConnection.MsgCenter.MessagingConfiguration.UseMessageBatching)
            {
                RunBatch();
            }
            else
            {
                RunNonBatch();
            }
        }

        protected void RunNonBatch()
        {
            try
            {
                while (!Cts.IsCancellationRequested)
                {
                    int bytesRead = FillBuffer(buffer.RecieveBuffer);
                    if (bytesRead == 0)
                    {
                        continue;
                    }

                    buffer.UpdateReceivedData(bytesRead);

                    Message msg;
                    while (buffer.TryDecodeMessage(out msg))
                    {
                        gatewayConnection.MsgCenter.QueueIncomingMessage(msg);
                        if (Log.IsVerbose3) Log.Verbose3("Received a message from gateway {0}: {1}", gatewayConnection.Address, msg);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ErrorCode.ProxyClientUnhandledExceptionWhileReceiving, String.Format("Unexpected/unhandled exception while receiving: {0}. Restarting gateway receiver for {1}.",
                    ex, gatewayConnection.Address), ex);
                throw;
            }
        }

        private void RunBatch()
        {
            /* Disable batching.  TODO: Remove or fix
            try
            {
                var metaHeader = new byte[Message.LENGTH_META_HEADER];
                var metaHeaderSegments = new List<ArraySegment<byte>> { new ArraySegment<byte>(metaHeader) };

                while (!Cts.IsCancellationRequested)
                {
                    if (!FillBuffer(metaHeaderSegments, Message.LENGTH_META_HEADER))
                    {
                        continue;
                    }

                    var numberOfMessages = BitConverter.ToInt32(metaHeader, 0);
                    var lengths = new byte[Message.LENGTH_HEADER_SIZE * numberOfMessages];
                    var lengthSegments = new List<ArraySegment<byte>> { new ArraySegment<byte>(lengths) };

                    if (!FillBuffer(lengthSegments, Message.LENGTH_HEADER_SIZE * numberOfMessages))
                    {
                        continue;
                    }

                    var headerLengths = new int[numberOfMessages];
                    var bodyLengths = new int[numberOfMessages];
                    var headerbodiesLength = 0;
                    var headerbodies = new List<ArraySegment<byte>>();

                    for (int i = 0; i < numberOfMessages; i++)
                    {
                        headerLengths[i] = BitConverter.ToInt32(lengths, i * 8);
                        bodyLengths[i] = BitConverter.ToInt32(lengths, i * 8 + 4);
                        headerbodiesLength += (headerLengths[i] + bodyLengths[i]);

                        // We need to set the boundary of ArraySegment<byte>s to the same as the header/body boundary
                        headerbodies.AddRange(BufferPool.GlobalPool.GetMultiBuffer(headerLengths[i]));
                        headerbodies.AddRange(BufferPool.GlobalPool.GetMultiBuffer(bodyLengths[i]));
                    }

                    if (!FillBuffer(headerbodies, headerbodiesLength))
                    {
                        continue;
                    }

                    var lengthSoFar = 0;
                    for (int i = 0; i < numberOfMessages; i++)
                    {
                        var header = ByteArrayBuilder.BuildSegmentListWithLengthLimit(headerbodies, lengthSoFar, headerLengths[i]);
                        var body = ByteArrayBuilder.BuildSegmentListWithLengthLimit(headerbodies, lengthSoFar + headerLengths[i], bodyLengths[i]);
                        lengthSoFar += (headerLengths[i] + bodyLengths[i]);

                        var msg = new Message(header, body);

                        MessagingStatisticsGroup.OnMessageReceive(msg, headerLengths[i], bodyLengths[i]);

                        if (Log.IsVerbose3) Log.Verbose3("Received a message from gateway {0}: {1}", gatewayConnection.Address, msg);
                        gatewayConnection.MsgCenter.QueueIncomingMessage(msg);
                    }
                    MessagingStatisticsGroup.OnMessageBatchReceive(SocketDirection.ClientToGateway, numberOfMessages, lengthSoFar);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(ErrorCode.ProxyClientUnhandledExceptionWhileReceiving, String.Format("Unexpected/unhandled exception while receiving: {0}. Restarting gateway receiver for {1}.",
                    ex, gatewayConnection.Address), ex);
                throw;
            }
             */
        }

        /* Disable batching.  TODO: Remove or fix
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private bool FillBuffer(List<ArraySegment<byte>> buffer, int length)
        {
            var offset = 0;

            while (offset < length)
            {
                Socket socketCapture = null;
                if (Cts.IsCancellationRequested)
                {
                    return false;
                }

                try
                {
                    if (gatewayConnection.Socket == null || !gatewayConnection.Socket.Connected)
                    {
                        gatewayConnection.Connect();
                    }
                    socketCapture = gatewayConnection.Socket;
                    if (socketCapture != null && socketCapture.Connected)
                    {
                        var bytesRead = socketCapture.Receive(ByteArrayBuilder.BuildSegmentList(buffer, offset));
                        if (bytesRead == 0)
                        {
                            throw new EndOfStreamException("Socket closed");
                        }
                        offset += bytesRead;
                    }
                }
                catch (Exception ex)
                {
                    // Only try to reconnect if we're not shutting down
                    if (Cts.IsCancellationRequested) return false;

                    if (ex is SocketException)
                    {
                        Log.Warn(ErrorCode.Runtime_Error_100158, "Exception receiving from gateway " + gatewayConnection.Address);
                    }
                    gatewayConnection.MarkAsDisconnected(socketCapture);
                    return false;
                }
            }
            return true;
        }
         */

        private int FillBuffer(List<ArraySegment<byte>> buffer)
        {
            Socket socketCapture = null;
            try
            {
                if (gatewayConnection.Socket == null || !gatewayConnection.Socket.Connected)
                {
                    gatewayConnection.Connect();
                }
                socketCapture = gatewayConnection.Socket;
                if (socketCapture != null && socketCapture.Connected)
                {
                    var bytesRead = socketCapture.Receive(buffer);
                    if (bytesRead == 0)
                    {
                        throw new EndOfStreamException("Socket closed");
                    }
                    return bytesRead;
                }
            }
            catch (Exception ex)
            {
                // Only try to reconnect if we're not shutting down
                if (Cts.IsCancellationRequested) return 0;

                if (ex is SocketException)
                {
                    Log.Warn(ErrorCode.Runtime_Error_100158, "Exception receiving from gateway " + gatewayConnection.Address);
                }
                gatewayConnection.MarkAsDisconnected(socketCapture);
                return 0;
            }
            return 0;
        }
    }
}
