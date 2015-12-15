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
            : base(gateway.Address.ToString())
        {
            gatewayConnection = gateway;
            OnFault = FaultBehavior.RestartOnFault;
            buffer = new IncomingMessageBuffer(Log, true); 
        }

        protected override void Run()
        {
            if (gatewayConnection.MsgCenter.MessagingConfiguration.UseMessageBatching)
            {
                throw new OrleansException("UseMessageBatching is no longer supported for ClientReceiver.");
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
                    int bytesRead = FillBuffer(buffer.ReceiveBuffer);
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

                Log.Warn(ErrorCode.Runtime_Error_100158, String.Format("Exception receiving from gateway {0}: {1}", gatewayConnection.Address, ex.Message));
                gatewayConnection.MarkAsDisconnected(socketCapture);
                return 0;
            }
            return 0;
        }
    }
}
