using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.WireProtocol;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class FloatCodec : TypedCodecBase<float, FloatCodec>, IFieldCodec<float>
    {
        private static readonly Type CodecFieldType = typeof(float);

        void IFieldCodec<float>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            float value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, float value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed32);

            // TODO: Optimize
            writer.WriteUInt32((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));
        }

        float IFieldCodec<float>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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

                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NETCOREAPP
        public static float ReadFloatRaw<TInput>(ref Reader<TInput> reader) => BitConverter.Int32BitsToSingle(reader.ReadInt32());
#else
        public static float ReadFloatRaw<TInput>(ref Reader<TInput> reader) => BitConverter.ToSingle(BitConverter.GetBytes(reader.ReadInt32()), 0);
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new ArgumentOutOfRangeException(
            $"{nameof(wireType)} {wireType} is not supported by this codec.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowValueOutOfRange<T>(T value) => throw new ArgumentOutOfRangeException(
            $"The {typeof(T)} value has a magnitude too high {value} to be converted to {typeof(float)}.");
    }

    [RegisterCopier]
    public sealed class FloatCopier : IDeepCopier<float>
    {
        public float DeepCopy(float input, CopyContext _) => input;
    }

    [RegisterSerializer]
    public sealed class DoubleCodec : TypedCodecBase<double, DoubleCodec>, IFieldCodec<double>
    {
        private static readonly Type CodecFieldType = typeof(double);

        void IFieldCodec<double>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            double value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, double value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.Fixed64);

            // TODO: Optimize
            writer.WriteUInt64((ulong)BitConverter.ToInt64(BitConverter.GetBytes(value), 0));
        }

        double IFieldCodec<double>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#if NETCOREAPP
        public static double ReadDoubleRaw<TInput>(ref Reader<TInput> reader) => BitConverter.Int64BitsToDouble(reader.ReadInt64());
#else
        public static double ReadDoubleRaw<TInput>(ref Reader<TInput> reader) => BitConverter.ToDouble(BitConverter.GetBytes(reader.ReadInt64()), 0);
#endif

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new ArgumentOutOfRangeException(
            $"{nameof(wireType)} {wireType} is not supported by this codec.");
    }

    [RegisterCopier]
    public sealed class DoubleCopier : IDeepCopier<double>
    {
        public double DeepCopy(double input, CopyContext _) => input;
    }

    [RegisterSerializer]
    public sealed class DecimalCodec : TypedCodecBase<decimal, DecimalCodec>, IFieldCodec<decimal>
    {
        private const int Width = 16;
        private static readonly Type CodecFieldType = typeof(decimal);

        void IFieldCodec<decimal>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, decimal value) => WriteField(ref writer, fieldIdDelta, expectedType, value);

        public static void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, decimal value) where TBufferWriter : IBufferWriter<byte>
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, CodecFieldType, WireType.LengthPrefixed);
            writer.WriteVarUInt32(Width);
            var holder = new DecimalConverter
            {
                Value = value
            };
            writer.WriteUInt64(holder.First);
            writer.WriteUInt64(holder.Second);
        }

        decimal IFieldCodec<decimal>.ReadValue<TInput>(ref Reader<TInput> reader, Field field) => ReadValue(ref reader, field);

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
                default:
                    ThrowWireTypeOutOfRange(field.WireType);
                    return 0;
            }
        }

        public static decimal ReadDecimalRaw<TInput>(ref Reader<TInput> reader)
        {
            var length = reader.ReadVarUInt32();
            if (length != Width)
            {
                throw new UnexpectedLengthPrefixValueException("decimal", Width, length);
            }

            var first = reader.ReadUInt64();
            var second = reader.ReadUInt64();
            var holder = new DecimalConverter
            {
                First = first,
                Second = second,
            };

            // This could be retrieved from the Value property of the holder, but it is safer to go through the constructor to ensure that validation occurs early.
            return new decimal(holder.Lo, holder.Mid, holder.Hi, holder.IsNegative, holder.Scale);
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct DecimalConverter
        {
            [FieldOffset(0)] public decimal Value;

            [FieldOffset(0)] public ulong First;
            [FieldOffset(8)] public ulong Second;

            [FieldOffset(0)] private int Flags;
            [FieldOffset(4)] public int Hi;
            [FieldOffset(8)] public int Lo;
            [FieldOffset(12)] public int Mid;

            public byte Scale => (byte)(Flags >> 16);

            public bool IsNegative => (Flags & unchecked((int)0x80000000)) != 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowWireTypeOutOfRange(WireType wireType) => throw new ArgumentOutOfRangeException(
            $"{nameof(wireType)} {wireType} is not supported by this codec.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowValueOutOfRange<T>(T value) => throw new ArgumentOutOfRangeException(
            $"The {typeof(T)} value has a magnitude too high {value} to be converted to {typeof(decimal)}.");
    }

    [RegisterCopier]
    public sealed class DecimalCopier : IDeepCopier<decimal>
    {
        public decimal DeepCopy(decimal input, CopyContext _) => input;
    }
}