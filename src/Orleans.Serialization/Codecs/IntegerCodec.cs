using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Utilities;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="bool"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class BoolCodec : TypedCodecBase<bool, BoolCodec>, IFieldCodec<bool>, IDeepCopier<bool>
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

        /// <inheritdoc/>
        public bool DeepCopy(bool input, CopyContext _) => input; 
    }

    /// <summary>
    /// Serializer for <see cref="char"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class CharCodec : TypedCodecBase<char, CharCodec>, IFieldCodec<char>, IDeepCopier<char>
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

        /// <inheritdoc/>
        public char DeepCopy(char input, CopyContext _) => input; 
    }

    /// <summary>
    /// Serializer for <see cref="byte"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class ByteCodec : TypedCodecBase<byte, ByteCodec>, IFieldCodec<byte>, IDeepCopier<byte>
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

        /// <inheritdoc/>
        public byte DeepCopy(byte input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="sbyte"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class SByteCodec : TypedCodecBase<sbyte, SByteCodec>, IFieldCodec<sbyte>, IDeepCopier<sbyte>
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

        /// <inheritdoc/>
        public sbyte DeepCopy(sbyte input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="ushort"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UInt16Codec : TypedCodecBase<ushort, UInt16Codec>, IFieldCodec<ushort>, IDeepCopier<ushort>
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

        /// <inheritdoc/>
        public ushort DeepCopy(ushort input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="short"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class Int16Codec : TypedCodecBase<short, Int16Codec>, IFieldCodec<short>, IDeepCopier<short>
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

        /// <inheritdoc/>
        public short DeepCopy(short input, CopyContext _) => input;
    }

    /// <summary>
    /// Serialzier for <see cref="uint"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UInt32Codec : TypedCodecBase<uint, UInt32Codec>, IFieldCodec<uint>, IDeepCopier<uint>
    {
        private static readonly Type CodecFieldType = typeof(uint);

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
            if (value > 1 << 20)
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
            if (value > 1 << 20)
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

        /// <inheritdoc/>
        public uint DeepCopy(uint input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="int"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class Int32Codec : TypedCodecBase<int, Int32Codec>, IFieldCodec<int>, IDeepCopier<int>
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
                writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.Fixed32);
                writer.WriteInt32(value);
            }
            else
            {
                writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.VarInt);
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

        /// <inheritdoc/>
        public int DeepCopy(int input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="long"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class Int64Codec : TypedCodecBase<long, Int64Codec>, IFieldCodec<long>, IDeepCopier<long>
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
            if (value <= int.MaxValue && value >= int.MinValue)
            {
                if (value > 1 << 20 || -value > 1 << 20)
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
            else if (value > 1 << 41 || -value > 1 << 41)
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
            if (value <= int.MaxValue && value >= int.MinValue)
            {
                if (value > 1 << 20 || -value > 1 << 20)
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
            else if (value > 1 << 41 || -value > 1 << 41)
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

        /// <inheritdoc/>
        public long DeepCopy(long input, CopyContext _) => input;
    }

    /// <summary>
    /// Serializer for <see cref="long"/>.
    /// </summary>
    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UInt64Codec : TypedCodecBase<ulong, UInt64Codec>, IFieldCodec<ulong>, IDeepCopier<ulong>
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
            if (value <= int.MaxValue)
            {
                if (value > 1 << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);
                    writer.WriteUInt32((uint)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.VarInt);
                    writer.WriteVarUInt64(value);
                }
            }
            else if (value > 1 << 41)
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
            if (value <= int.MaxValue)
            {
                if (value > 1 << 20)
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.Fixed32);
                    writer.WriteUInt32((uint)value);
                }
                else
                {
                    writer.WriteFieldHeader(fieldIdDelta, expectedType, actualType, WireType.VarInt);
                    writer.WriteVarUInt64(value);
                }
            }
            else if (value > 1 << 41)
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

        /// <inheritdoc/>
        public ulong DeepCopy(ulong input, CopyContext _) => input;
    }
}