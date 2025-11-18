using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.DurableJobs.AzureStorage;

/// <summary>
/// Provides methods for serializing and deserializing JSON data using the netstring format.
/// Netstrings are a simple, self-delimiting way to encode data with length prefixes.
/// Format: [6 hex digits]:[data]\n
/// Maximum data size is 10MB (0xA00000 bytes).
/// </summary>
public static class NetstringJsonSerializer<T>
{
    private const int MaxLength = 0xA00000; // 10MB

    /// <summary>
    /// Encodes an object as a netstring by serializing it to JSON and writing directly to a stream.
    /// </summary>
    /// <param name="value">The object to encode.</param>
    /// <param name="stream">The stream to write the netstring-encoded data to.</param>
    /// <param name="jsonTypeInfo">The JSON type info for serialization.</param>
    /// <exception cref="InvalidOperationException">Thrown when the serialized data exceeds the maximum length.</exception>
    public static void Encode(T value, Stream stream, JsonTypeInfo<T> jsonTypeInfo)
    {
        // Remember starting position
        var startPosition = stream.Position;

        // Skip past where the length prefix will go (6 hex digits + colon)
        Span<byte> lengthBytes = stackalloc byte[7];
        stream.Write(lengthBytes);

        // Remember position where data starts
        var dataStartPosition = stream.Position;
        
        // Serialize JSON directly to stream
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = false }))
        {
            JsonSerializer.Serialize(writer, value, jsonTypeInfo);
        }

        stream.Flush();
        
        // Calculate JSON length
        var jsonLength = (int)(stream.Position - dataStartPosition);
        
        if (jsonLength > MaxLength)
        {
            throw new InvalidOperationException($"Serialized data exceeds maximum length of {MaxLength} bytes");
        }

        // Write trailing newline
        stream.WriteByte((byte)'\n');
        
        // Remember end position
        var endPosition = stream.Position;
        
        // Seek back to write the length prefix
        stream.Position = startPosition;
        
        // Format length as 6-digit hex and write directly
        if (!Utf8Formatter.TryFormat(jsonLength, lengthBytes, out _, new StandardFormat('X', 6)))
        {
            throw new InvalidOperationException("Failed to format length prefix");
        }

        lengthBytes[6] = (byte)':';
        
        stream.Write(lengthBytes);
        
        // Restore position to end
        stream.Position = endPosition;
    }

    /// <summary>
    /// Reads netstring-encoded JSON objects from a stream and deserializes them.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="jsonTypeInfo">The JSON type info for deserialization.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>An async enumerable of deserialized objects.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid netstring data.</exception>
    public static async IAsyncEnumerable<T> DecodeAsync(Stream stream, JsonTypeInfo<T> jsonTypeInfo, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int TypicalBufferSize = 4096; // 4KB 
        var buffer = ArrayPool<byte>.Shared.Rent(TypicalBufferSize);

        try
        {
            while (true)
            {
                
                // Try to read length prefix (6 hex digits + colon)
                try
                {
                    await stream.ReadExactlyAsync(buffer, 0, 7, cancellationToken);
                }
                catch (EndOfStreamException)
                {
                    // We are done
                    yield break;
                }

                // Verify colon
                if (buffer[6] != ':')
                {
                    throw new InvalidDataException($"Expected colon at position 6, got byte value {buffer[6]}");
                }

                // Parse length as hex
                if (!Utf8Parser.TryParse(buffer.AsSpan(0, 6), out int length, out _, 'X'))
                {
                    throw new InvalidDataException($"Invalid netstring length: {System.Text.Encoding.UTF8.GetString(buffer, 0, 6)}");
                }

                if (length < 0 || length > MaxLength)
                {
                    throw new InvalidDataException($"Netstring length out of valid range: {length}");
                }

                // Ensure buffer is large enough for the data + newline
                var totalLength = length + 1;
                if (buffer.Length < totalLength)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(totalLength);
                }

                // Read data + trailing newline
                try
                {
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, totalLength), cancellationToken);
                }
                catch (EndOfStreamException ex)
                {
                    throw new InvalidDataException("Unexpected end of stream while reading netstring data", ex);
                }

                // Verify trailing newline
                if (buffer[length] != '\n')
                {
                    throw new InvalidDataException($"Expected newline at end of netstring, got byte value {buffer[length]}");
                }

                // Deserialize JSON directly from UTF-8 bytes
                var result = JsonSerializer.Deserialize(buffer.AsSpan(0, length), jsonTypeInfo);
                if (result is null)
                {
                    throw new JsonException("Deserialized JSON resulted in null value");
                }

                yield return result;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
