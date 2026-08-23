using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Orleans.EntityFrameworkCore;

internal static class EFCoreIdentifierHash
{
    internal static byte[] Compute(params string[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var byteCount = 0;
        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            byteCount = checked(byteCount + sizeof(int) + Encoding.UTF8.GetByteCount(value));
        }

        var input = new byte[byteCount];
        var position = 0;
        foreach (var value in values)
        {
            var valueByteCount = Encoding.UTF8.GetByteCount(value);
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(position, sizeof(int)), valueByteCount);
            position += sizeof(int);
            position += Encoding.UTF8.GetBytes(value, input.AsSpan(position, valueByteCount));
        }

        return SHA256.HashData(input);
    }
}
