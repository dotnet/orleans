using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs.AzureStorage;

/// <summary>
/// Provides methods for serializing and deserializing JSON data using the netstring format.
/// Netstrings are a simple, self-delimiting way to encode data with length prefixes.
/// Format: [length]:[data]\n
/// </summary>
public static class NetstringJsonSerializer
{
    /// <summary>
    /// Encodes an object as a netstring by serializing it to JSON.
    /// </summary>
    /// <typeparam name="T">The type of the object to encode.</typeparam>
    /// <param name="value">The object to encode.</param>
    /// <param name="jsonTypeInfo">The JSON type info for serialization.</param>
    /// <returns>A byte array containing the netstring-encoded JSON data.</returns>
    public static byte[] Encode<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        // Serialize to JSON using ArrayBufferWriter to avoid allocations
        var bufferWriter = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            JsonSerializer.Serialize(writer, value, jsonTypeInfo);
        }

        var jsonBytes = bufferWriter.WrittenSpan;
        var jsonLength = jsonBytes.Length;

        // Calculate length prefix size
        Span<byte> lengthBuffer = stackalloc byte[20]; // Max int32 digits + colon
        if (!Utf8Formatter.TryFormat(jsonLength, lengthBuffer, out var lengthBytesWritten))
        {
            throw new InvalidOperationException("Failed to format length prefix");
        }

        lengthBuffer[lengthBytesWritten++] = (byte)':';

        // Allocate final result array
        var totalLength = lengthBytesWritten + jsonLength + 1; // +1 for newline
        var result = new byte[totalLength];

        // Copy components
        lengthBuffer[..lengthBytesWritten].CopyTo(result);
        jsonBytes.CopyTo(result.AsSpan(lengthBytesWritten));
        result[^1] = (byte)'\n';

        return result;
    }

    /// <summary>
    /// Reads netstring-encoded JSON objects from a stream and deserializes them.
    /// </summary>
    /// <typeparam name="T">The type of objects to deserialize.</typeparam>
    /// <param name="stream">The stream to read from.</param>
    /// <param name="jsonTypeInfo">The JSON type info for deserialization.</param>
    /// <returns>An async enumerable of deserialized objects.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid netstring data.</exception>
    /// <exception cref="JsonException">Thrown when the JSON data cannot be deserialized.</exception>
    public static async IAsyncEnumerable<T> DecodeAsync<T>(Stream stream, JsonTypeInfo<T> jsonTypeInfo)
    {
        var lengthBuffer = ArrayPool<byte>.Shared.Rent(20); // Max int32 digits

        try
        {
            while (true)
            {
                // Read length prefix using Utf8Parser
                var lengthBufferPos = 0;
                while (true)
                {
                    var b = stream.ReadByte();
                    if (b == -1)
                    {
                        if (lengthBufferPos == 0)
                        {
                            yield break; // Clean end of stream
                        }

                        throw new InvalidDataException("Unexpected end of stream while reading netstring length");
                    }

                    if (b == ':')
                    {
                        break;
                    }

                    if (lengthBufferPos >= lengthBuffer.Length)
                    {
                        throw new InvalidDataException("Netstring length prefix too long");
                    }

                    lengthBuffer[lengthBufferPos++] = (byte)b;
                }

                if (lengthBufferPos == 0)
                {
                    throw new InvalidDataException("Empty netstring length prefix");
                }

                // Parse length using Utf8Parser
                if (!Utf8Parser.TryParse(new ReadOnlySpan<byte>(lengthBuffer, 0, lengthBufferPos), out int length, out var bytesConsumed) || bytesConsumed != lengthBufferPos)
                {
                    throw new InvalidDataException($"Invalid netstring length: {System.Text.Encoding.UTF8.GetString(lengthBuffer, 0, lengthBufferPos)}");
                }

                if (length < 0)
                {
                    throw new InvalidDataException($"Netstring length cannot be negative: {length}");
                }

                // Read data using ArrayPool to avoid allocation
                var buffer = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    var totalRead = 0;
                    while (totalRead < length)
                    {
                        var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead));
                        if (read == 0)
                        {
                            throw new InvalidDataException("Unexpected end of stream while reading netstring data");
                        }

                        totalRead += read;
                    }

                    // Read trailing newline
                    var newline = stream.ReadByte();
                    if (newline != '\n')
                    {
                        throw new InvalidDataException($"Expected newline at end of netstring, got byte value {newline}");
                    }

                    // Deserialize JSON directly from UTF-8 bytes
                    var result = JsonSerializer.Deserialize(new ReadOnlySpan<byte>(buffer, 0, length), jsonTypeInfo);
                    if (result is null)
                    {
                        throw new JsonException("Deserialized JSON resulted in null value");
                    }

                    yield return result;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBuffer);
        }
    }
}
