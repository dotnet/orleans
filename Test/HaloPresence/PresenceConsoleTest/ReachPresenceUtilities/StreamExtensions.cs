using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using Corinth.Blf;
using Corinth.Blf.Serialization;

namespace ReachPresence.Utilities
{
    public static class StreamExtensions
    {
        private const int BUF_LENGTH = 8192;

        public static MemoryStream SlurpToMemoryStream(this Stream stream)
        {
            byte[] buffer = new byte[BUF_LENGTH];
            using (var ms = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : BUF_LENGTH))
            {
                int read;
                do
                {
                    read = stream.Read(buffer, 0, BUF_LENGTH);
                    ms.Write(buffer, 0, read);
                } while (read > 0);

                ms.Position = 0;

                return ms;
            }
        }

        public static byte[] SlurpToByteArray(this Stream stream)
        {
            using (var ms = SlurpToMemoryStream(stream))
                return ms.ToArray();
        }

        public static Chunk DeserializeChunk(this Stream stream)
        {
            using (MemoryStream memStr = stream.SlurpToMemoryStream())
            {
                var reader = new BinaryReaderBigEndian(memStr);
                return BlfFile.DeserializeChunk(reader, reader);
            }
        }

        public static void SerializeChunk(this Stream stream, Chunk chunk)
        {
            using (MemoryStream memStr = new MemoryStream())
            {
                var writer = new BinaryWriterBigEndian(memStr);
                BlfFile.SerializeChunk(writer, writer, chunk);
                memStr.WriteTo(stream);
            }
        }

        public static BlfFile DeserializeBlfFile(this Stream stream)
        {
            using (MemoryStream memStr = stream.SlurpToMemoryStream())
            {
                BlfFile file = new BlfFile();
                file.Deserialize(memStr);
                return file;
            }
        }

        public static void SerializeBlfFile(this Stream stream, BlfFile file)
        {
            using (MemoryStream blfStream = new MemoryStream())
            {
                file.Serialize(blfStream);
                blfStream.WriteTo(stream);
            }
        }
    }
}
