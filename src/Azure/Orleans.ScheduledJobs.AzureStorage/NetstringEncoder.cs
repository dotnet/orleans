using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Orleans.ScheduledJobs.AzureStorage;

/// <summary>
/// Provides methods for encoding and decoding data using the netstring format.
/// Netstrings are a simple, self-delimiting way to encode strings.
/// Format: [length]:[data]\n
/// </summary>
public static class NetstringEncoder
{
    /// <summary>
    /// Encodes a string as a netstring.
    /// </summary>
    /// <param name="data">The string to encode.</param>
    /// <returns>A byte array containing the netstring-encoded data.</returns>
    /// <remarks>
    /// TODO: Optimize using ArrayPool&lt;byte&gt;, Utf8Formatter, and stackalloc to reduce allocations.
    /// </remarks>
    public static byte[] Encode(string data)
    {
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(data);
        var lengthPrefix = System.Text.Encoding.UTF8.GetBytes($"{dataBytes.Length}:");
        var result = new byte[lengthPrefix.Length + dataBytes.Length + 1];
        
        lengthPrefix.CopyTo(result, 0);
        dataBytes.CopyTo(result, lengthPrefix.Length);
        result[^1] = (byte)'\n';
        
        return result;
    }

    /// <summary>
    /// Reads netstring-encoded strings from a stream.
    /// </summary>
    /// <param name="stream">The stream to read from.</param>
    /// <returns>An async enumerable of decoded strings.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream contains invalid netstring data.</exception>
    /// <remarks>
    /// TODO: Optimize using ArrayPool&lt;byte&gt;, Utf8Parser, and return ReadOnlyMemory&lt;byte&gt; to reduce allocations.
    /// </remarks>
    public static async IAsyncEnumerable<string> DecodeAsync(Stream stream)
    {
        while (true)
        {
            // Read length prefix (as bytes, not chars)
            var lengthStr = "";
            while (true)
            {
                var b = stream.ReadByte();
                if (b == -1)
                {
                    yield break;
                }
                
                if (b == ':')
                {
                    break;
                }
                
                lengthStr += (char)b;
            }

            if (string.IsNullOrWhiteSpace(lengthStr))
            {
                yield break;
            }

            if (!int.TryParse(lengthStr, out var length))
            {
                throw new InvalidDataException($"Invalid netstring length: {lengthStr}");
            }

            if (length < 0)
            {
                throw new InvalidDataException($"Netstring length cannot be negative: {length}");
            }

            // Read data as bytes
            var buffer = new byte[length];
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
                throw new InvalidDataException($"Expected newline at end of netstring, got '{(char)newline}'");
            }

            yield return System.Text.Encoding.UTF8.GetString(buffer);
        }
    }
}
