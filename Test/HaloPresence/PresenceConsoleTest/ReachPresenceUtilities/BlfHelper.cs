using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Corinth.Blf;
using Corinth.Blf.Serialization;


namespace ReachPresence.Utilities
{
    public static class BlfHelper
    {
        public static byte[] SerializeChunk(Chunk chunk)
        {
            using (var output = new MemoryStream())
            {
                var writer = new BinaryWriterBigEndian(output);
                BlfFile.SerializeChunk(writer, writer, chunk);
                output.Seek(0, SeekOrigin.Begin);
                return output.SlurpToByteArray();
            }
        }

        public static byte[] SerializeChunk(Chunk chunk, int chunkSize)
        {
            using (var output = new MemoryStream(chunkSize))
            {
                var writer = new BinaryWriterBigEndian(output);
                BlfFile.SerializeChunk(writer, writer, chunk);
                output.Seek(0, SeekOrigin.Begin);
                return output.SlurpToByteArray();
            }
        }

        public static Chunk DeserializeChunk(byte[] chunkBytes)
        {
            using (var input = new MemoryStream())
            {
                input.Write(chunkBytes, 0, chunkBytes.Length);
                input.Seek(0, SeekOrigin.Begin);

                var reader = new BinaryReaderBigEndian(input);

                return BlfFile.DeserializeChunk(reader, reader);
            }
        }

        public static Chunk DeserializeChunk(byte[] chunkBytes, out bool containsMultipleChunks)
        {
            using (var input = new MemoryStream())
            {
                input.Write(chunkBytes, 0, chunkBytes.Length);
                input.Seek(0, SeekOrigin.Begin);

                var reader = new BinaryReaderBigEndian(input);

                var chunk = BlfFile.DeserializeChunk(reader, reader);

                if (input.Position < input.Length)
                    containsMultipleChunks = true;
                else
                    containsMultipleChunks = false;

                return chunk;
            }
        }
    }
}
