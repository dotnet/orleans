using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Queue;
using Orleans.Serialization;

namespace Orleans.Runtime.Host.Providers.Streams.AzureQueue
{
    internal class MessageSegment
    {
        internal Guid Guid { get; set; }

        internal byte Index { get; set; }

        internal byte Count { get; set; }

        internal ushort Size { get; set; }

        internal CloudQueueMessage CloudQueueMessage { get; set; }

        internal byte[] Segment { get; set; }

        internal static IEnumerable<MessageSegment> CreateRange(BinaryTokenStreamWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            if (writer.CurrentOffset == 0)
            {
                throw new ArgumentException("The data length was 0", nameof(writer));
            }

            var list = ResizeByteArrayBuilder(writer.ByteArrayBuilder);
            var guid = Guid.NewGuid();

            return list.Select((t, i) => new MessageSegment
            {
                Count = (byte)list.Count,
                Guid = guid,
                Index = (byte)i,
                Segment = t.ToArray(),
                Size = (ushort) t.Count
            });
        }

        internal static MessageSegment FromCloudQueueMessage(CloudQueueMessage message)
        {
            ushort size;
            var reader = new BinaryTokenStreamReader(message.AsBytes);
            return new MessageSegment
            {
                Guid = reader.ReadGuid(),
                Index = reader.ReadByte(),
                Count = reader.ReadByte(),
                Size = size = reader.ReadUShort(),
                Segment = reader.ReadBytes(size),
                CloudQueueMessage = message
            };
        }

        internal byte[] ToByteArray()
        {
            var writer = new BinaryTokenStreamWriter();
            writer.Write(this.Guid);
            writer.Write(this.Index);
            writer.Write(this.Count);
            writer.Write(this.Size);
            writer.Write(this.Segment);
            return writer.ToByteArray();
        }

        private static List<ArraySegment<byte>> ResizeByteArrayBuilder(ByteArrayBuilder byteArrayBuilder)
        {
            // resize the arrays in the byte array builder so that they are all under the max cloud queue message size
            // Doing it this way will prevent allocations on the large object heap.
            const int reservedSpace = 1024 * 20;  
            var maxBufferSize = (int)(CloudQueueMessage.MaxMessageSize - reservedSpace);
            var list = byteArrayBuilder.ToBytes();
            var returnValue = new List<ArraySegment<byte>>();

            if (list.Count == 0)
            {
                return list;
            }

            if (byteArrayBuilder.Length <= maxBufferSize)
            {
                return list;
            }

            if (!list.Any(l => l.Count > maxBufferSize))
            {
                return list;
            }

            ArraySegment<byte>? segmentTemp = null;
            foreach (var segment in list)
            {
                if (!segmentTemp.HasValue || segmentTemp.Value.Count == 0)
                {
                    segmentTemp = segment;
                }
                else
                {
                    // create a buffer for the remaining portion of the previous segment and the first part of the next segment
                    var bufferSize = Math.Min(maxBufferSize, segmentTemp.Value.Count + segment.Count);
                    var countFromNewSegment = bufferSize - segmentTemp.Value.Count;
                    var buffer = new byte[bufferSize];

                    // copy rest of previous segment
                    Array.Copy(segmentTemp.Value.Array, segmentTemp.Value.Offset, buffer, 0, segmentTemp.Value.Count);
                    
                    // copy beginning of currentSegment;
                    Array.Copy(segment.Array, segment.Offset, buffer, segmentTemp.Value.Count, countFromNewSegment);

                    returnValue.Add(new ArraySegment<byte>(buffer));
                    segmentTemp = new ArraySegment<byte>(segment.Array, segment.Offset + countFromNewSegment, segment.Count - countFromNewSegment);
                }

                while (segmentTemp.Value.Count >= maxBufferSize)
                {
                    returnValue.Add(new ArraySegment<byte>(segmentTemp.Value.Array, segmentTemp.Value.Offset, maxBufferSize));
                    segmentTemp = new ArraySegment<byte>(segmentTemp.Value.Array, segmentTemp.Value.Offset + maxBufferSize, segmentTemp.Value.Count - maxBufferSize);
                }
            }

            if (segmentTemp.HasValue && segmentTemp.Value.Count > 0)
            {
                returnValue.Add(segmentTemp.Value);
            } 

            return returnValue;
        }
    }
}
