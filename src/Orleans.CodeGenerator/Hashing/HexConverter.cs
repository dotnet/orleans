#nullable enable
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;

namespace Orleans.CodeGenerator.Hashing
{
    internal static class HexConverter
    {
        public static unsafe string ToString(ReadOnlySpan<byte> bytes)
        {
            // Adapted from: https://github.com/dotnet/runtime/blob/f156fb9dcf121e536b93ae90bcc5e8e6d5336062/src/libraries/Common/src/System/HexConverter.cs#L196
            
            Span<char> result = bytes.Length > 16 ?
                new char[bytes.Length * 2].AsSpan() :
                stackalloc char[bytes.Length * 2];

            int pos = 0;
            foreach (byte b in bytes)
            {
                ToCharsBuffer(b, result, pos);
                pos += 2;
            }

            return result.ToString();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            static void ToCharsBuffer(byte value, Span<char> buffer, int startingIndex = 0)
            {
                var difference = ((value & 0xF0U) << 4) + (value & 0x0FU) - 0x8989U;
                var packedResult = (((uint)-(int)difference & 0x7070U) >> 4) + difference + 0xB9B9U;

                buffer[startingIndex + 1] = (char)(packedResult & 0xFF);
                buffer[startingIndex] = (char)(packedResult >> 8);
            }
        }
    }
}