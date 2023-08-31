using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Codecs
{
    /// <summary>
    /// Serializer for <see cref="float"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class FloatCodec : IFieldCodec<float>
    {
        void IFieldCodec<float>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, float value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(float), WireType.Fixed32);
#if NET6_0_OR_GREATER
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(value));
#else
            writer.WriteUInt32((uint)BitConverter.SingleToInt32Bits(value));
#endif
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, float value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.Fixed32);
#if NET6_0_OR_GREATER
            writer.WriteUInt32(BitConverter.SingleToUInt32Bits(value));
#else
            writer.WriteUInt32((uint)BitConverter.SingleToInt32Bits(value));
#endif
        }

        /// <inheritdoc/>
        float IFieldCodec<float>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static float ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            switch (field.WireType)
            {
                case WireType.Fixed32:
                    return ReadFloatRaw(ref reader);
                case WireType.Fixed64:
                    {
                        var value = DoubleCodec.ReadDoubleRaw(ref reader);
                        if ((value > float.MaxValue || value < float.MinValue) && !double.IsInfinity(value) && !double.IsNaN(value))
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (float)value;
                    }

                case WireType.LengthPrefixed:
                    return (float)DecimalCodec.ReadDecimalRaw(ref reader);
#if NET6_0_OR_GREATER
                case WireType.VarInt:
                    return (float)HalfCodec.ReadHalfRaw(ref reader);
#endif
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        /// <summary>
        /// Reads a value without any protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET6_0_OR_GREATER
        public static float ReadFloatRaw<TInput>(ref Reader<TInput> reader) => BitConverter.UInt32BitsToSingle(reader.ReadUInt32());
#else
        public static float ReadFloatRaw<TInput>(ref Reader<TInput> reader) => BitConverter.Int32BitsToSingle((int)reader.ReadUInt32());
#endif

        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new UnsupportedWireTypeException(
            $"WireType {wireType} is not supported by this codec.");

        private static void ThrowValueOutOfRange<T>(T value) => throw new OverflowException(
            $"The {typeof(T)} value has a magnitude too high {value} to be converted to {typeof(float)}.");
    }

    /// <summary>
    /// Serializer for <see cref="double"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class DoubleCodec : IFieldCodec<double>
    {
        void IFieldCodec<double>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, double value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(double), WireType.Fixed64);
#if NET6_0_OR_GREATER
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(value));
#else
            writer.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(value));
#endif
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, double value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.Fixed64);
#if NET6_0_OR_GREATER
            writer.WriteUInt64(BitConverter.DoubleToUInt64Bits(value));
#else
            writer.WriteUInt64((ulong)BitConverter.DoubleToInt64Bits(value));
#endif
        }

        /// <inheritdoc/>
        double IFieldCodec<double>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static double ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            switch (field.WireType)
            {
                case WireType.Fixed32:
                    return FloatCodec.ReadFloatRaw(ref reader);
                case WireType.Fixed64:
                    return ReadDoubleRaw(ref reader);
                case WireType.LengthPrefixed:
                    return (double)DecimalCodec.ReadDecimalRaw(ref reader);
#if NET6_0_OR_GREATER
                case WireType.VarInt:
                    return (double)HalfCodec.ReadHalfRaw(ref reader);
#endif
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        /// <summary>
        /// Reads a value without any protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NET6_0_OR_GREATER
        public static double ReadDoubleRaw<TInput>(ref Reader<TInput> reader) => BitConverter.UInt64BitsToDouble(reader.ReadUInt64());
#else
        public static double ReadDoubleRaw<TInput>(ref Reader<TInput> reader) => BitConverter.Int64BitsToDouble((long)reader.ReadUInt64());
#endif

        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new UnsupportedWireTypeException(
            $"WireType {wireType} is not supported by this codec.");
    }

    /// <summary>
    /// Serializer for <see cref="decimal"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class DecimalCodec : IFieldCodec<decimal>
    {
        private const int Width = 16;

        void IFieldCodec<decimal>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, decimal value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(decimal), WireType.LengthPrefixed);
            WriteRaw(ref writer, ref value);
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, decimal value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(fieldIdDelta, WireType.LengthPrefixed);
            WriteRaw(ref writer, ref value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, ref decimal value) where TBufferWriter : IBufferWriter<byte>
        {
            writer.WriteVarUInt7(Width);

#if NET6_0_OR_GREATER
            if (BitConverter.IsLittleEndian)
            {
                writer.Write(MemoryMarshal.AsBytes(new Span<decimal>(ref value)));
                return;
            }
#endif

            ref var holder = ref Unsafe.As<decimal, DecimalConverter>(ref value);
            writer.WriteUInt32(holder.Flags);
            writer.WriteUInt32(holder.Hi32);
            writer.WriteUInt64(holder.Lo64);
        }

        /// <inheritdoc/>
        decimal IFieldCodec<decimal>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static decimal ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            switch (field.WireType)
            {
                case WireType.Fixed32:
                    {
                        var value = FloatCodec.ReadFloatRaw(ref reader);
                        if (value > (float)decimal.MaxValue || value < (float)decimal.MinValue)
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (decimal)value;
                    }
                case WireType.Fixed64:
                    {
                        var value = DoubleCodec.ReadDoubleRaw(ref reader);
                        if (value > (double)decimal.MaxValue || value < (double)decimal.MinValue)
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (decimal)value;
                    }
                case WireType.LengthPrefixed:
                    return ReadDecimalRaw(ref reader);
#if NET6_0_OR_GREATER
                case WireType.VarInt:
                    return (decimal)HalfCodec.ReadHalfRaw(ref reader);
#endif
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        /// <summary>
        /// Reads a value without protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        public static decimal ReadDecimalRaw<TInput>(ref Reader<TInput> reader)
        {
            var length = reader.ReadVarUInt32();
            if (length != Width)
            {
                throw new UnexpectedLengthPrefixValueException("decimal", Width, length);
            }

#if NET6_0_OR_GREATER
            if (BitConverter.IsLittleEndian)
            {
                Unsafe.SkipInit(out decimal res);
                reader.ReadBytes(MemoryMarshal.AsBytes(new Span<decimal>(ref res)));
                return res;
            }
#endif

            DecimalConverter holder;
            holder.Flags = reader.ReadUInt32();
            holder.Hi32 = reader.ReadUInt32();
            holder.Lo64 = reader.ReadUInt64();
            return Unsafe.As<DecimalConverter, decimal>(ref holder);
        }

        private struct DecimalConverter
        {
            public uint Flags;
            public uint Hi32;
            public ulong Lo64;
        }

        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new UnsupportedWireTypeException(
            $"WireType {wireType} is not supported by this codec.");

        private static void ThrowValueOutOfRange<T>(T value) => throw new OverflowException(
            $"The {typeof(T)} value has a magnitude too high {value} to be converted to {typeof(decimal)}.");
    }

#if NET6_0_OR_GREATER
    /// <summary>
    /// Serializer for <see cref="Half"/>.
    /// </summary>
    [RegisterSerializer]
    public sealed class HalfCodec : IFieldCodec<Half>
    {
        void IFieldCodec<Half>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Half value)
        {
            var asUShort = BitConverter.HalfToUInt16Bits(value);
            UInt16Codec.WriteField(ref writer, fieldIdDelta, expectedType, asUShort, typeof(Half));
        }

        /// <summary>
        /// Writes a field without type info (expected type is statically known).
        /// </summary>
        /// <typeparam name="TBufferWriter">The buffer writer type.</typeparam>
        /// <param name="writer">The writer.</param>
        /// <param name="fieldIdDelta">The field identifier delta.</param>
        /// <param name="value">The value.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Half value) where TBufferWriter : IBufferWriter<byte>
        {
            var asUShort = BitConverter.HalfToUInt16Bits(value);
            UInt16Codec.WriteField(ref writer, fieldIdDelta, asUShort);
        }

        /// <inheritdoc/>
        Half IFieldCodec<Half>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

        /// <summary>
        /// Reads a value.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <param name="field">The field.</param>
        /// <returns>The value.</returns>
        public static Half ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            ReferenceCodec.MarkValueField(reader.Session);
            switch (field.WireType)
            {
                case WireType.VarInt:
                    return ReadHalfRaw(ref reader);
                case WireType.Fixed32:
                    {
                        var value = FloatCodec.ReadFloatRaw(ref reader);
                        if ((value > (float)Half.MaxValue || value < (float)Half.MinValue) && !float.IsInfinity(value) && !float.IsNaN(value))
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (Half)value;
                    }
                case WireType.Fixed64:
                    {
                        var value = DoubleCodec.ReadDoubleRaw(ref reader);
                        if ((value > (double)Half.MaxValue || value < (double)Half.MinValue) && !double.IsInfinity(value) && !double.IsNaN(value))
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (Half)value;
                    }

                case WireType.LengthPrefixed:
                    {
                        var value = DecimalCodec.ReadDecimalRaw(ref reader);
                        if (value > (decimal)Half.MaxValue || value < (decimal)Half.MinValue)
                        {
                            ThrowValueOutOfRange(value);
                        }

                        return (Half)value;
                    }
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return default;
            }
        }

        /// <summary>
        /// Reads a value without protocol framing.
        /// </summary>
        /// <typeparam name="TInput">The reader input type.</typeparam>
        /// <param name="reader">The reader.</param>
        /// <returns>The value.</returns>
        internal static Half ReadHalfRaw<TInput>(ref Reader<TInput> reader) => BitConverter.UInt16BitsToHalf(reader.ReadVarUInt16());

        [DoesNotReturn]
        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new UnsupportedWireTypeException(
            $"WireType {wireType} is not supported by this codec.");

        [DoesNotReturn]
        private static void ThrowValueOutOfRange<T>(T value) => throw new OverflowException(
            $"The {typeof(T)} value has a magnitude too high {value} to be converted to {typeof(Half)}.");
    }
#endif
}