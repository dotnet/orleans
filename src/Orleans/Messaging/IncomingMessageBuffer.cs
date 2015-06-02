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

namespace Orleans.Runtime
{
    internal class IncomingMessageBuffer
    {
        public List<ArraySegment<byte>> RecieveBuffer
        {
            get { return ByteArrayBuilder.BuildSegmentList(readBuffer, recieveOffset); }
        }

        private const int Kb = 1024;
        private const int DEFAULT_RECEIVE_BUFFER_SIZE = 128 * Kb; // 128k
        private const int DEFAULT_MAX_SUSTAINED_RECEIVE_BUFFER_SIZE = DEFAULT_RECEIVE_BUFFER_SIZE * 8; // 1mg
        private const int GROW_MAX_BLOCK_SIZE = 1024 * Kb; // 1mg
        private readonly List<ArraySegment<byte>> readBuffer;
        private readonly int maxSustainedBufferSize;
        private int currentBufferSize;

        private readonly byte[] lengthBuffer;

        private int headerLength;
        private int bodyLength;

        private int recieveOffset;
        private int decodeOffset;

        private readonly bool supportForwarding;
        private TraceLogger Log;

        public IncomingMessageBuffer(TraceLogger logger, bool supportForwarding = false, int receiveBufferSize = DEFAULT_RECEIVE_BUFFER_SIZE, int maxSustainedReceiveBufferSize = DEFAULT_MAX_SUSTAINED_RECEIVE_BUFFER_SIZE)
        {
            Log = logger;
            this.supportForwarding = supportForwarding;
            currentBufferSize = receiveBufferSize;
            maxSustainedBufferSize = maxSustainedReceiveBufferSize;
            lengthBuffer = new byte[Message.LENGTH_HEADER_SIZE];
            readBuffer = BufferPool.GlobalPool.GetMultiBuffer(currentBufferSize);
            recieveOffset = 0;
            decodeOffset = 0;
            headerLength = 0;
            bodyLength = 0;
        }

        public void UpdateReceivedData(int bytesRead)
        {
            recieveOffset += bytesRead;
        }

        public void Reset()
        {
            recieveOffset = 0;
            decodeOffset = 0;
            headerLength = 0;
            bodyLength = 0;
        }

        public bool TryDecodeMessage(out Message msg)
        {
            msg = null;

            // Is there enough read into the buffer to continue (at least read the lengths?)
            if (recieveOffset - decodeOffset < KnownMessageSize())
                return false;

            // parse lengths if needed
            if (headerLength == 0 || bodyLength == 0)
            {
                // get length segments
                List<ArraySegment<byte>> lenghts = ByteArrayBuilder.BuildSegmentListWithLengthLimit(readBuffer, decodeOffset, Message.LENGTH_HEADER_SIZE);

                // copy length segment to buffer
                int lengthBufferoffset = 0;
                foreach (ArraySegment<byte> seg in lenghts)
                {
                    Buffer.BlockCopy(seg.Array, seg.Offset, lengthBuffer, lengthBufferoffset, seg.Count);
                    lengthBufferoffset += seg.Count;
                }

                // read lengths
                headerLength = BitConverter.ToInt32(lengthBuffer, 0);
                bodyLength = BitConverter.ToInt32(lengthBuffer, 4);
            }

            // If message is too big for default buffer size, grow
            while (decodeOffset + KnownMessageSize() > currentBufferSize)
            {
                //TODO: Add configurable max message size for safety
                //TODO: Review networking layer and add max size checks to all dictionaries, arrays, or other variable sized containers.
                // double buffer size up to max grow block size, then only grow it in those intervals
                int growBlockSize = Math.Min(currentBufferSize, GROW_MAX_BLOCK_SIZE);
                readBuffer.AddRange(BufferPool.GlobalPool.GetMultiBuffer(growBlockSize));
                currentBufferSize += growBlockSize;
            }

            // Is there enough read into the buffer to read full message
            if (recieveOffset - decodeOffset < KnownMessageSize())
                return false;

            // decode header
            int headerOffset = decodeOffset + Message.LENGTH_HEADER_SIZE;
            List<ArraySegment<byte>> header = ByteArrayBuilder.BuildSegmentListWithLengthLimit(readBuffer, headerOffset, headerLength);

            // decode body
            int bodyOffset = headerOffset + headerLength;
            List<ArraySegment<byte>> body = ByteArrayBuilder.BuildSegmentListWithLengthLimit(readBuffer, bodyOffset, bodyLength);

            // need to maintain ownership of buffer, so if we are supporting forwarding we need to duplicate the body buffer.
            if (supportForwarding)
            {
                body = DuplicateBuffer(body);
            }

            // build message
            msg = new Message(header, body, !supportForwarding);
            MessagingStatisticsGroup.OnMessageReceive(msg, headerLength, bodyLength);

            if (headerLength + bodyLength > Message.LargeMessageSizeThreshold)
            {
                Log.Info(ErrorCode.Messaging_LargeMsg_Incoming, "Receiving large message Size={0} HeaderLength={1} BodyLength={2}. Msg={3}",
                    headerLength + bodyLength, headerLength, bodyLength, msg.ToString());
                if (Log.IsVerbose3) Log.Verbose3("Received large message {0}", msg.ToLongString());
            }

            // update parse recieveOffset and clear lengths
            decodeOffset = bodyOffset + bodyLength;
            headerLength = 0;
            bodyLength = 0;

            // drop buffers consumed in message and adjust parse recieveOffset
            // TODO: This can be optimized further. Linked lists?
            int consumedBytes = 0;
            while (readBuffer.Count != 0)
            {
                ArraySegment<byte> seg = readBuffer[0];
                if (seg.Count <= decodeOffset - consumedBytes)
                {
                    consumedBytes += seg.Count;
                    readBuffer.Remove(seg);
                    BufferPool.GlobalPool.Release(seg.Array);
                }
                else
                {
                    break;
                }
            }
            decodeOffset -= consumedBytes;
            recieveOffset -= consumedBytes;

            if (consumedBytes != 0)
            {
                if (currentBufferSize <= maxSustainedBufferSize)
                {
                    readBuffer.AddRange(BufferPool.GlobalPool.GetMultiBuffer(consumedBytes));
                }
                else
                {
                    // shrink buffer to DEFAULT_MAX_SUSTAINED_RECEIVE_BUFFER_SIZE
                    int backfillBytes = Math.Max(consumedBytes + maxSustainedBufferSize - currentBufferSize, 0);
                    currentBufferSize -= consumedBytes;
                    currentBufferSize += backfillBytes;
                    if (backfillBytes > 0)
                    {
                        readBuffer.AddRange(BufferPool.GlobalPool.GetMultiBuffer(backfillBytes));
                    }
                }
            }

            return true;
        }

        private int KnownMessageSize()
        {
            return headerLength + bodyLength + Message.LENGTH_HEADER_SIZE;
        }

        private List<ArraySegment<byte>> DuplicateBuffer(List<ArraySegment<byte>> body)
        {
            var dupBody = new List<ArraySegment<byte>>(body.Count);
            foreach (ArraySegment<byte> seg in body)
            {
                var dupSeg = new ArraySegment<byte>(BufferPool.GlobalPool.GetBuffer(), seg.Offset, seg.Count);
                Buffer.BlockCopy(seg.Array, seg.Offset, dupSeg.Array, dupSeg.Offset, seg.Count);
                dupBody.Add(dupSeg);
            }
            return dupBody;
        }
    }
}
