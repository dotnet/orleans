using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Storage
{
    /// <summary>
    /// Grain storage serializer that uses the Orleans <see cref="Serializer"/>.
    /// </summary>
    public class OrleansGrainStorageSerializer : IGrainStorageSerializer, IGrainStorageStreamingSerializer
    {
        private readonly Serializer serializer;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrleansGrainStorageSerializer"/> class.
        /// </summary>
        /// <param name="serializer">The serializer.</param>
        public OrleansGrainStorageSerializer(Serializer serializer)
        {
            this.serializer = serializer;
        }

        /// <inheritdoc/>
        public BinaryData Serialize<T>(T value)
        {
            var buffer = new ArrayBufferWriter<byte>();
            this.serializer.Serialize(value, buffer);
            return new BinaryData(buffer.WrittenMemory);
        }

        /// <inheritdoc/>
        public T Deserialize<T>(BinaryData input)
        {
            return this.serializer.Deserialize<T>(input.ToMemory());
        }

        /// <inheritdoc/>
        public ValueTask SerializeAsync<T>(T value, Stream destination, CancellationToken cancellationToken = default)
        {
            this.serializer.Serialize(value, destination);
            return ValueTask.CompletedTask;
        }

        /// <inheritdoc/>
        public async ValueTask<T> DeserializeAsync<T>(Stream input, CancellationToken cancellationToken = default)
        {
            // Seekable streams (e.g., MemoryStream, FileStream) can be deserialized directly without buffering.
            // Non-seekable streams (e.g., NetworkStream) require buffering to enable efficient multi-pass reading.
            if (input.CanSeek)
            {
                return this.serializer.Deserialize<T>(input);
            }

            var bufferStream = PooledBufferStream.Rent();
            try
            {
                await input.CopyToAsync(bufferStream, cancellationToken).ConfigureAwait(false);
                var sequence = bufferStream.RentReadOnlySequence();
                try
                {
                    return this.serializer.Deserialize<T>(sequence);
                }
                finally
                {
                    bufferStream.ReturnReadOnlySequence(sequence);
                }
            }
            finally
            {
                PooledBufferStream.Return(bufferStream);
            }
        }
    }
}
