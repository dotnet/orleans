using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="bool"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class BoolCodec : IFieldCodec<bool>
    {
        private static readonly Type CodecFieldType = typeof(bool);

        /// <inheritdoc/>
        void IFieldCodec<bool>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            bool value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, bool value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
            writer.WriteVarUInt32(value ? 1U : 0U);
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
            return reader.ReadUInt8(field.WireType) == 1;
        }
    }

    /// <summary>
    /// Serializer for <see cref="char"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class CharCodec : IFieldCodec<char>
    {
        private static readonly Type CodecFieldType = typeof(char);

        /// <inheritdoc/>
        void IFieldCodec<char>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            char value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, char value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
            writer.WriteVarUInt32(value);
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
        private static readonly Type CodecFieldType = typeof(byte);

        /// <inheritdoc/>
        void IFieldCodec<byte>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            byte value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, byte value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
            writer.WriteVarUInt32(value);
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
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
            writer.WriteVarUInt32(value);
        }

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
        private static readonly Type CodecFieldType = typeof(sbyte);

        /// <inheritdoc/>
        void IFieldCodec<sbyte>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            sbyte value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, sbyte value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
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
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
            writer.WriteVarInt8(value);
        }

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
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(ushort);

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

        /// <inheritdoc/>
        void IFieldCodec<ushort>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            ushort value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ushort value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
            writer.WriteVarUInt32(value);
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
            writer.WriteVarUInt32(value);
        }
    }

    /// <summary>
    /// Serializer for <see cref="short"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int16Codec : IFieldCodec<short>
    {
        private static readonly Type CodecFieldType = typeof(short);

        /// <inheritdoc/>
        void IFieldCodec<short>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            short value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, short value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
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
        public static readonly Type CodecFieldType = typeof(uint);

        /// <inheritdoc/>
        void IFieldCodec<uint>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            uint value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, uint value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value > 1U << 20)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                writer.WriteUInt32(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                writer.WriteVarUInt32(value);
            }
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
            if (value > 1U << 20)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed32);
                writer.WriteUInt32(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                writer.WriteVarUInt32(value);
            }
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
        /// <summary>
        /// The codec field type
        /// </summary>
        public static readonly Type CodecFieldType = typeof(int);

        /// <inheritdoc/>
        void IFieldCodec<int>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            int value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value > 1 << 20 || -value > 1 << 20)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                writer.WriteInt32(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                writer.WriteVarInt32(value);
            }
        }

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            int value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value > 1 << 20 || -value > 1 << 20)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                writer.WriteInt32(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                writer.WriteVarInt32(value);
            }
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
        public static void WriteField<TBufferWriter>(
            ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            int value,
            Type actualType) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value > 1 << 20 || -value > 1 << 20)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed32);
                writer.WriteInt32(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                writer.WriteVarInt32(value);
            }
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
        private static readonly Type CodecFieldType = typeof(long);

        /// <inheritdoc/>
        void IFieldCodec<long>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, long value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, long value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value is <= int.MaxValue and >= int.MinValue)
            {
                if (value > 1L << 20 || -value > 1L << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                    writer.WriteInt32((int)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                    writer.WriteVarInt64(value);
                }
            }
            else if (value > 1L << 41 || -value > 1L << 41)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);
                writer.WriteInt64(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                writer.WriteVarInt64(value);
            }
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
            if (value is <= int.MaxValue and >= int.MinValue)
            {
                if (value > 1L << 20 || -value > 1L << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed32);
                    writer.WriteInt32((int)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                    writer.WriteVarInt64(value);
                }
            }
            else if (value > 1L << 41 || -value > 1L << 41)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed64);
                writer.WriteInt64(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                writer.WriteVarInt64(value);
            }
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
        private static readonly Type CodecFieldType = typeof(ulong);

        /// <inheritdoc/>
        void IFieldCodec<ulong>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            ulong value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ulong value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            if (value <= uint.MaxValue)
            {
                if (value > 1UL << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                    writer.WriteUInt32((uint)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                    writer.WriteVarUInt32((uint)value);
                }
            }
            else if (value > 1UL << 41)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);
                writer.WriteUInt64(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                writer.WriteVarUInt64(value);
            }
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
            if (value <= uint.MaxValue)
            {
                if (value > 1UL << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed32);
                    writer.WriteUInt32((uint)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                    writer.WriteVarUInt32((uint)value);
                }
            }
            else if (value > 1UL << 41)
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed64);
                writer.WriteUInt64(value);
            }
            else
            {
                writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                writer.WriteVarUInt64(value);
            }
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

    /// <summary>
    /// Serializer for <see cref="Int128"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class Int128Codec : IFieldCodec<Int128>
    {
        private static readonly Type CodecFieldType = typeof(Int128);

        /// <inheritdoc/>
        void IFieldCodec<Int128>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Int128 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Int128 value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value <= (Int128)long.MaxValue && value >= (Int128)long.MinValue)
            {
                Int64Codec.WriteField(ref writer, fieldIdDelta, expectedType, (long)value, CodecFieldType);
            }
            else
            {
                ReferenceCodec.MarkValueField(writer.Session);
                var byteCount = ((IBinaryInteger<Int128>)value).GetByteCount();
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
                writer.WriteVarUInt32((uint)byteCount);
                Span<byte> bytes = byteCount < 64 ? stackalloc byte[64].Slice(0, byteCount) : new byte[byteCount];
                ((IBinaryInteger<Int128>)value).TryWriteLittleEndian(bytes, out var bytesWritten);
                Debug.Assert(bytesWritten == byteCount);
                writer.Write(bytes);
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
            ReadOnlySpan<byte> bytes;
            if (!reader.TryReadBytes((int)byteCount, out bytes))
            {
                bytes = reader.ReadBytes(byteCount);
            }

            if (!TryReadLittleEndian<Int128>(bytes, out var value))
            {
                ThrowReadFailure();
            }

            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadLittleEndian<T>(ReadOnlySpan<byte> source, out T value) where T : IBinaryInteger<T> => T.TryReadLittleEndian(source, isUnsigned: false, out value);

        [DoesNotReturn]
        private static void ThrowReadFailure() => throw new ArgumentException($"Failed to read {nameof(Int128)} value");
    }

    /// <summary>
    /// Serializer for <see cref="UInt128"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class UInt128Codec : IFieldCodec<UInt128>
    {
        private static readonly Type CodecFieldType = typeof(UInt128);

        /// <inheritdoc/>
        void IFieldCodec<UInt128>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UInt128 value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        /// <summary>
        /// Writes a field.
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="expectedType">The expected type.</param>
        /// <param name="value">The value.</param>
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UInt128 value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value <= (UInt128)ulong.MaxValue)
            {
                UInt64Codec.WriteField(ref writer, fieldIdDelta, expectedType, (ulong)value, CodecFieldType);
            }
            else
            {
                ReferenceCodec.MarkValueField(writer.Session);
                var byteCount = ((IBinaryInteger<UInt128>)value).GetByteCount();
                writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
                writer.WriteVarUInt32((uint)byteCount);
                Span<byte> bytes = byteCount < 64 ? stackalloc byte[64].Slice(0, byteCount) : new byte[byteCount];
                ((IBinaryInteger<UInt128>)value).TryWriteLittleEndian(bytes, out var bytesWritten);
                Debug.Assert(bytesWritten == byteCount);
                writer.Write(bytes);
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
            ReadOnlySpan<byte> bytes;
            if (!reader.TryReadBytes((int)byteCount, out bytes))
            {
                bytes = reader.ReadBytes(byteCount);
            }

            if (!TryReadLittleEndian<UInt128>(bytes, out var value))
            {
                ThrowReadFailure();
            }

            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryReadLittleEndian<T>(ReadOnlySpan<byte> source, out T value) where T : IBinaryInteger<T> => T.TryReadLittleEndian(source, isUnsigned: true, out value);

        [DoesNotReturn]
        private static void ThrowReadFailure() => throw new ArgumentException($"Failed to read {nameof(UInt128)} value");
    }
}
