using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="bool"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class BoolCodec : IFieldCodec<bool>
    {
        void IFieldCodec<bool>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, bool value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(bool), WireType.VarInt);
            writer.WriteByte(value ? (byte)3 : (byte)1); // writer.WriteVarUInt32(value ? 1U : 0U);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, bool value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteByte(value ? (byte)3 : (byte)1); // writer.WriteVarUInt32(value ? 1U : 0U);
        }

        /// <inheritdoc/>
        bool IFieldCodec<bool>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadUInt8(field.WireType) != 0;
        }
    }

    /// <summary>
    /// Serializer for <see cref="char"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class CharCodec : IFieldCodec<char>
    {
        void IFieldCodec<char>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, char value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(char), WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, char value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <inheritdoc/>
        char IFieldCodec<char>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static char ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return (char)reader.ReadUInt16(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="byte"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class ByteCodec : IFieldCodec<byte>
    {
        void IFieldCodec<byte>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(byte), WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, byte value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte value, Type actualType) where TBufferWriter : IBufferWriter<byte>
            => UInt16Codec.WriteField(ref writer, fieldIdDelta, expectedType, value, actualType);

        /// <inheritdoc/>
        byte IFieldCodec<byte>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadUInt8(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="sbyte"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class SByteCodec : IFieldCodec<sbyte>
    {
        void IFieldCodec<sbyte>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, sbyte value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(sbyte), WireType.VarInt);
            writer.WriteVarInt8(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, sbyte value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteVarInt8(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, sbyte value, Type actualType) where TBufferWriter : IBufferWriter<byte>
            => Int16Codec.WriteField(ref writer, fieldIdDelta, expectedType, value, actualType);

        /// <inheritdoc/>
        sbyte IFieldCodec<sbyte>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static sbyte ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadInt8(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="ushort"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UInt16Codec : IFieldCodec<ushort>
    {
        /// <inheritdoc/>
        ushort IFieldCodec<ushort>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadUInt16(field.WireType);
        }

        void IFieldCodec<ushort>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ushort value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ushort), WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, ushort value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteVarUInt28(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ushort value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
            writer.WriteVarUInt28(value);
        }
    }

    /// <summary>
    /// Serializer for <see cref="short"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int16Codec : IFieldCodec<short>
    {
        void IFieldCodec<short>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, short value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(short), WireType.VarInt);
            writer.WriteVarInt16(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, short value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
            writer.WriteVarInt16(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, short value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
            writer.WriteVarInt16(value);
        }

        /// <inheritdoc/>
        short IFieldCodec<short>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static short ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadInt16(field.WireType);
        }
    }

    /// <summary>
    /// Serialzier for <see cref="uint"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UInt32Codec : IFieldCodec<uint>
    {
        void IFieldCodec<uint>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, uint value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(uint), value < 1 << 21 ? WireType.VarInt : WireType.Fixed32);

            if (value < 1 << 21) writer.WriteVarUInt28(value);
            else writer.WriteUInt32(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, uint value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, value < 1 << 21 ? WireType.VarInt : WireType.Fixed32);

            if (value < 1 << 21) writer.WriteVarUInt28(value);
            else writer.WriteUInt32(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, uint value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, value < 1 << 21 ? WireType.VarInt : WireType.Fixed32);

            if (value < 1 << 21) writer.WriteVarUInt28(value);
            else writer.WriteUInt32(value);
        }

        /// <inheritdoc/>
        uint IFieldCodec<uint>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadUInt32(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="int"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int32Codec : IFieldCodec<int>
    {
        void IFieldCodec<int>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, int value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value < 1 << 20 && value > -1 << 20 ? WireType.VarInt : WireType.Fixed32;
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(int), wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt28(Writer<TBufferWriter>.ZigZagEncode(value));
            else writer.WriteInt32(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, int value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value < 1 << 20 && value > -1 << 20 ? WireType.VarInt : WireType.Fixed32;
            writer.WriteFieldHeaderExpected(fieldIdDelta, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt28(Writer<TBufferWriter>.ZigZagEncode(value));
            else writer.WriteInt32(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, int value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value < 1 << 20 && value > -1 << 20 ? WireType.VarInt : WireType.Fixed32;
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt28(Writer<TBufferWriter>.ZigZagEncode(value));
            else writer.WriteInt32(value);
        }

        /// <inheritdoc/>
        int IFieldCodec<int>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadInt32(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="long"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int64Codec : IFieldCodec<long>
    {
        void IFieldCodec<long>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, long value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 20 and > -1 << 20 => WireType.VarInt,
                <= int.MaxValue and >= int.MinValue => WireType.Fixed32,
                < 1L << 48 and > -1L << 48 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(long), wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(Writer<TBufferWriter>.ZigZagEncode(value));
            else if (wireType == WireType.Fixed32) writer.WriteInt32((int)value);
            else writer.WriteInt64(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, long value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 20 and > -1 << 20 => WireType.VarInt,
                <= int.MaxValue and >= int.MinValue => WireType.Fixed32,
                < 1L << 48 and > -1L << 48 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeaderExpected(fieldIdDelta, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(Writer<TBufferWriter>.ZigZagEncode(value));
            else if (wireType == WireType.Fixed32) writer.WriteInt32((int)value);
            else writer.WriteInt64(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, long value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 20 and > -1 << 20 => WireType.VarInt,
                <= int.MaxValue and >= int.MinValue => WireType.Fixed32,
                < 1L << 48 and > -1L << 48 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(Writer<TBufferWriter>.ZigZagEncode(value));
            else if (wireType == WireType.Fixed32) writer.WriteInt32((int)value);
            else writer.WriteInt64(value);
        }

        /// <inheritdoc/>
        long IFieldCodec<long>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadInt64(field.WireType);
        }
    }

    /// <summary>
    /// Serializer for <see cref="long"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UInt64Codec : IFieldCodec<ulong>
    {
        void IFieldCodec<ulong>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ulong value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 21 => WireType.VarInt,
                <= uint.MaxValue => WireType.Fixed32,
                < 1UL << 49 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(ulong), wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(value);
            else if (wireType == WireType.Fixed32) writer.WriteUInt32((uint)value);
            else writer.WriteUInt64(value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, ulong value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 21 => WireType.VarInt,
                <= uint.MaxValue => WireType.Fixed32,
                < 1UL << 49 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeaderExpected(fieldIdDelta, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(value);
            else if (wireType == WireType.Fixed32) writer.WriteUInt32((uint)value);
            else writer.WriteUInt64(value);
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        /// <param name="actualType">The actual type.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ulong value, Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            var wireType = value switch
            {
                < 1 << 21 => WireType.VarInt,
                <= uint.MaxValue => WireType.Fixed32,
                < 1UL << 49 => WireType.VarInt,
                _ => WireType.Fixed64,
            };
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, wireType);

            if (wireType == WireType.VarInt) writer.WriteVarUInt56(value);
            else if (wireType == WireType.Fixed32) writer.WriteUInt32((uint)value);
            else writer.WriteUInt64(value);
        }

        /// <inheritdoc/>
        ulong IFieldCodec<ulong>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            return reader.ReadUInt64(field.WireType);
        }
    }

#if NET7_0_OR_GREATER
    /// <summary>
    /// Serializer for <see cref="Int128"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int128Codec : IFieldCodec<Int128>
    {
        /// <inheritdoc/>
        void IFieldCodec<Int128>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Int128 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Int128 value) where TBufferWriter : IBufferWriter<byte>
            => WriteField(ref writer, fieldIdDelta, typeof(Int128), value);

        private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Int128 value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value <= (Int128)long.MaxValue && value >= (Int128)long.MinValue)
            {
                Int64Codec.WriteField(ref writer, fieldIdDelta, expectedType, (long)value, typeof(Int128));
            }
            else
            {
                ReferenceCodec.MarkValueField(writer.Session);
                const int byteCount = 128 / 8;
                writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(Int128), WireType.LengthPrefixed);
                writer.WriteVarUInt7(byteCount);
                if (BitConverter.IsLittleEndian)
                {
                    writer.Write(MemoryMarshal.AsBytes(new Span<Int128>(ref value)));
                }
                else
                {
                    writer.EnsureContiguous(byteCount);
                    ((IBinaryInteger<Int128>)value).TryWriteLittleEndian(writer.WritableSpan, out var bytesWritten);
                    Debug.Assert(bytesWritten == byteCount);
                    writer.AdvanceSpan(byteCount);
                }
            }
        }

        /// <inheritdoc/>
        Int128 IFieldCodec<Int128>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int128 ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType != WireType.LengthPrefixed)
            {
                return Int64Codec.ReadValue(ref reader, field);
            }

            ReferenceCodec.MarkValueField(reader.Session);
            return ReadRaw(ref reader);
        }

        /// <summary>
        /// Reads a value without protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        internal static Int128 ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var byteCount = reader.ReadVarUInt32();
            if (byteCount != 128 / 8) throw new UnexpectedLengthPrefixValueException(nameof(Int128), 128 / 8, byteCount);
            Unsafe.SkipInit(out Int128 res);
            var bytes = MemoryMarshal.AsBytes(new Span<Int128>(ref res));
            reader.ReadBytes(bytes);

            if (BitConverter.IsLittleEndian)
                return res;

            var done = TryReadLittleEndian<Int128>(bytes, out var value);
            Debug.Assert(done);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadLittleEndian<T>(ReadOnlySpan<byte> source, out T value) where T : IBinaryInteger<T> => T.TryReadLittleEndian(source, isUnsigned: false, out value);
    }

    /// <summary>
    /// Serializer for <see cref="UInt128"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UInt128Codec : IFieldCodec<UInt128>
    {
        /// <inheritdoc/>
        void IFieldCodec<UInt128>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UInt128 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, UInt128 value) where TBufferWriter : IBufferWriter<byte>
            => WriteField(ref writer, fieldIdDelta, typeof(UInt128), value);

        private static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UInt128 value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value <= (UInt128)ulong.MaxValue)
            {
                UInt64Codec.WriteField(ref writer, fieldIdDelta, expectedType, (ulong)value, typeof(UInt128));
            }
            else
            {
                ReferenceCodec.MarkValueField(writer.Session);
                const int byteCount = 128 / 8;
                writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(UInt128), WireType.LengthPrefixed);
                writer.WriteVarUInt7(byteCount);
                if (BitConverter.IsLittleEndian)
                {
                    writer.Write(MemoryMarshal.AsBytes(new Span<UInt128>(ref value)));
                }
                else
                {
                    writer.EnsureContiguous(byteCount);
                    ((IBinaryInteger<UInt128>)value).TryWriteLittleEndian(writer.WritableSpan, out var bytesWritten);
                    Debug.Assert(bytesWritten == byteCount);
                    writer.AdvanceSpan(byteCount);
                }
            }
        }

        /// <inheritdoc/>
        UInt128 IFieldCodec<UInt128>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static UInt128 ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType != WireType.LengthPrefixed)
            {
                return UInt64Codec.ReadValue(ref reader, field);
            }

            ReferenceCodec.MarkValueField(reader.Session);
            return ReadRaw(ref reader);
        }

        /// <summary>
        /// Reads a value without protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        internal static UInt128 ReadRaw<TInput>(ref Reader<TInput> reader)
        {
            var byteCount = reader.ReadVarUInt32();
            if (byteCount != 128 / 8) throw new UnexpectedLengthPrefixValueException(nameof(UInt128), 128 / 8, byteCount);
            Unsafe.SkipInit(out UInt128 res);
            var bytes = MemoryMarshal.AsBytes(new Span<UInt128>(ref res));
            reader.ReadBytes(bytes);

            if (BitConverter.IsLittleEndian)
                return res;

            var done = TryReadLittleEndian<UInt128>(bytes, out var value);
            Debug.Assert(done);
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadLittleEndian<T>(ReadOnlySpan<byte> source, out T value) where T : IBinaryInteger<T> => T.TryReadLittleEndian(source, isUnsigned: true, out value);
    }
#endif
}
