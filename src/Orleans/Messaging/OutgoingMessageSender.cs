using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using Orleans.Runtime;
using Orleans.Runtime.Configuration;


namespace Orleans.Messaging
{
    internal enum SocketDirection
    {
        SiloToSilo,
        ClientToGateway,
        GatewayToClient
    }

    internal abstract class OutgoingMessageSender : AsynchQueueAgent<Message>
    {
        internal OutgoingMessageSender(string nameSuffix, IMessagingConfiguration config)
            : base(nameSuffix, config)
        {
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected override void Process(Message msg)
        {
            if (Log.IsVerbose2) Log.Verbose2("Got a {0} message to send: {1}", msg.Direction, msg);
            bool continueSend = PrepareMessageForSend(msg);
            if (!continueSend) return;

            Socket sock;
            string error;
            SiloAddress targetSilo;
            continueSend = GetSendingSocket(msg, out sock, out targetSilo, out error);
            if (!continueSend)
            {
                OnGetSendingSocketFailure(msg, error);
                return;
            }

            List<ArraySegment<byte>> data;
            int headerLength = 0;
            try
            {
                data = msg.Serialize(out headerLength);
            }
            catch (Exception exc)
            {
                OnMessageSerializationFailure(msg, exc);
                return;
            }

            int length = data.Sum(x => x.Count);
            int bytesSent = 0;
            bool exceptionSending = false;
            bool countMismatchSending = false;
            string sendErrorStr = null;
            try
            {
                bytesSent = sock.Send(data);
                if (bytesSent != length)
                {
                    // The complete message wasn't sent, even though no error was reported; treat this as an error
                    countMismatchSending = true;
                    sendErrorStr = String.Format("Byte count mismatch on sending to {0}: sent {1}, expected {2}", targetSilo, bytesSent, length);
                    Log.Warn(ErrorCode.Messaging_CountMismatchSending, sendErrorStr);
                }
            }
            catch (Exception exc)
            {
                exceptionSending = true;
                if (!(exc is ObjectDisposedException))
                {
                    sendErrorStr = String.Format("Exception sending message to {0}. Message: {1}. {2}", targetSilo, msg, exc);
                    Log.Warn(ErrorCode.Messaging_ExceptionSending, sendErrorStr, exc);
                }
            }
            MessagingStatisticsGroup.OnMessageSend(targetSilo, msg.Direction, bytesSent, headerLength, GetSocketDirection());
            bool sendError = exceptionSending || countMismatchSending;
            if (sendError)
                OnSendFailure(sock, targetSilo);

            ProcessMessageAfterSend(msg, sendError, sendErrorStr);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        protected override void ProcessBatch(List<Message> msgs)
        {
            if (Log.IsVerbose2) Log.Verbose2("Got {0} messages to send.", msgs.Count);
            for (int i = 0; i < msgs.Count; )
            {
                bool sendThisMessage = PrepareMessageForSend(msgs[i]);
                if (sendThisMessage)
                    i++;
                else
                    msgs.RemoveAt(i); // don't advance i
            }

            if (msgs.Count <= 0) return;

            Socket sock;
            string error;
            SiloAddress targetSilo;
            bool continueSend = GetSendingSocket(msgs[0], out sock, out targetSilo, out error);
            if (!continueSend)
            {
                foreach (Message msg in msgs)
                    OnGetSendingSocketFailure(msg, error);

                return;
            }

            List<ArraySegment<byte>> data;
            int headerLength = 0;
            continueSend = SerializeMessages(msgs, out data, out headerLength, OnMessageSerializationFailure);
            if (!continueSend) return;

            int length = data.Sum(x => x.Count);
            int bytesSent = 0;
            bool exceptionSending = false;
            bool countMismatchSending = false;
            string sendErrorStr = null;
            try
            {
                bytesSent = sock.Send(data);
                if (bytesSent != length)
                {
                    // The complete message wasn't sent, even though no error was reported; treat this as an error
                    countMismatchSending = true;
                    sendErrorStr = String.Format("Byte count mismatch on sending to {0}: sent {1}, expected {2}", targetSilo, bytesSent, length);
                    Log.Warn(ErrorCode.Messaging_CountMismatchSending, sendErrorStr);
                }
            }
            catch (Exception exc)
            {
                exceptionSending = true;
                if (!(exc is ObjectDisposedException))
                {
                    sendErrorStr = String.Format("Exception sending message to {0}. {1}", targetSilo, TraceLogger.PrintException(exc));
                    Log.Warn(ErrorCode.Messaging_ExceptionSending, sendErrorStr, exc);
                }
            }
            MessagingStatisticsGroup.OnMessageBatchSend(targetSilo, msgs[0].Direction, bytesSent, headerLength, GetSocketDirection(), msgs.Count);
            bool sendError = exceptionSending || countMismatchSending;
            if (sendError)
                OnSendFailure(sock, targetSilo);

            foreach (Message msg in msgs)
                ProcessMessageAfterSend(msg, sendError, sendErrorStr);
        }

        public static bool SerializeMessages(List<Message> msgs, out List<ArraySegment<byte>> data, out int headerLengthOut, Action<Message, Exception> msgSerializationFailureHandler)
        {
            int numberOfValidMessages = 0;
            var lengths = new List<ArraySegment<byte>>();
            var bodies = new List<ArraySegment<byte>>();
            data = null;
            headerLengthOut = 0;
            int totalHeadersLen = 0;

            foreach(var message in msgs)
            {
                try
                {
                    int headerLength;
                    int bodyLength;
                    List<ArraySegment<byte>> body = message.SerializeForBatching(out headerLength, out bodyLength);
                    var headerLen = new ArraySegment<byte>(BitConverter.GetBytes(headerLength));
                    var bodyLen = new ArraySegment<byte>(BitConverter.GetBytes(bodyLength));

                    bodies.AddRange(body);
                    lengths.Add(headerLen);
                    lengths.Add(bodyLen);
                    numberOfValidMessages++;
                    totalHeadersLen += headerLength;
                }
                catch (Exception exc)
                {
                    if (msgSerializationFailureHandler!=null)
                        msgSerializationFailureHandler(message, exc);
                    else
                        throw;
                }
            }

            // at least 1 message was successfully serialized
            if (bodies.Count <= 0) return false;

            data = new List<ArraySegment<byte>> {new ArraySegment<byte>(BitConverter.GetBytes(numberOfValidMessages))};
            data.AddRange(lengths);
            data.AddRange(bodies);
            headerLengthOut = totalHeadersLen;
            return true;
            // no message serialized
        }

        protected abstract SocketDirection GetSocketDirection();
        protected abstract bool PrepareMessageForSend(Message msg);
        protected abstract bool GetSendingSocket(Message msg, out Socket socket, out SiloAddress targetSilo, out string error);
        protected abstract void OnGetSendingSocketFailure(Message msg, string error);
        protected abstract void OnMessageSerializationFailure(Message msg, Exception exc);
        protected abstract void OnSendFailure(Socket socket, SiloAddress targetSilo);
        protected abstract void ProcessMessageAfterSend(Message msg, bool sendError, string sendErrorStr);
        protected abstract void FailMessage(Message msg, string reason);
    }
}
