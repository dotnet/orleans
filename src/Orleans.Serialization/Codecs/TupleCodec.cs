using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.WireProtocol;
using System;
using System.Runtime.CompilerServices;

namespace Orleans.Serialization.Codecs
{
    [RegisterSerializer]
    public sealed class TupleCodec<T> : IFieldCodec<Tuple<T>>
    {
        private readonly Type CodecElementType = typeof(T);

        private readonly IFieldCodec<T> _valueCodec;

        public TupleCodec(IFieldCodec<T> valueCodec)
        {
            _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, valueCodec);
        }

        void IFieldCodec<Tuple<T>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Tuple<T> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _valueCodec.WriteField(ref writer, 1, CodecElementType, value.Item1);

            writer.WriteEndObject();
        }

        Tuple<T> IFieldCodec<Tuple<T>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _valueCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T>(item1);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }

    [RegisterCopier]
    public class TupleCopier<T> : IDeepCopier<Tuple<T>>
    {
        private readonly IDeepCopier<T> _copier;
        public TupleCopier(IDeepCopier<T> copier)
        {
            _copier = copier;
        }

        public Tuple<T> DeepCopy(Tuple<T> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T>))
            {
                return context.Copy(input);
            }

            result = new Tuple<T>(_copier.DeepCopy(input.Item1, context));
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class TupleCodec<T1, T2> : IFieldCodec<Tuple<T1, T2>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;

        public TupleCodec(IFieldCodec<T1> item1Codec, IFieldCodec<T2> item2Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
        }

        void IFieldCodec<Tuple<T1, T2>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Tuple<T1, T2> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);

            writer.WriteEndObject();
        }

        Tuple<T1, T2> IFieldCodec<Tuple<T1, T2>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2>(item1, item2);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2> : IDeepCopier<Tuple<T1, T2>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;

        public TupleCopier(IDeepCopier<T1> copier1, IDeepCopier<T2> copier2)
        {
            _copier1 = copier1;
            _copier2 = copier2;
        }

        public Tuple<T1, T2> DeepCopy(Tuple<T1, T2> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context));
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3> : IFieldCodec<Tuple<T1, T2, T3>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Tuple<T1, T2, T3> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);

            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3> IFieldCodec<Tuple<T1, T2, T3>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3>(item1, item2, item3);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2, T3> : IDeepCopier<Tuple<T1, T2, T3>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
        }

        public Tuple<T1, T2, T3> DeepCopy(Tuple<T1, T2, T3> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context));
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3, T4> : IFieldCodec<Tuple<T1, T2, T3, T4>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);
        private static readonly Type ElementType4 = typeof(T4);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec,
            IFieldCodec<T4> item4Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
            _item4Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item4Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3, T4>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Tuple<T1, T2, T3, T4> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);
            _item4Codec.WriteField(ref writer, 1, ElementType4, value.Item4);

            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3, T4> IFieldCodec<Tuple<T1, T2, T3, T4>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3, T4>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            var item4 = default(T4);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        item4 = _item4Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2, T3, T4> : IDeepCopier<Tuple<T1, T2, T3, T4>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3,
            IDeepCopier<T4> copier4)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
            _copier4 = copier4;
        }

        public Tuple<T1, T2, T3, T4> DeepCopy(Tuple<T1, T2, T3, T4> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3, T4> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3, T4>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context),
                _copier4.DeepCopy(input.Item4, context));
            context.RecordCopy(input, result);
            return result;
        }
    } 

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3, T4, T5> : IFieldCodec<Tuple<T1, T2, T3, T4, T5>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);
        private static readonly Type ElementType4 = typeof(T4);
        private static readonly Type ElementType5 = typeof(T5);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec,
            IFieldCodec<T4> item4Codec,
            IFieldCodec<T5> item5Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
            _item4Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item4Codec);
            _item5Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item5Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3, T4, T5>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            Tuple<T1, T2, T3, T4, T5> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);
            _item4Codec.WriteField(ref writer, 1, ElementType4, value.Item4);
            _item5Codec.WriteField(ref writer, 1, ElementType5, value.Item5);

            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3, T4, T5> IFieldCodec<Tuple<T1, T2, T3, T4, T5>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3, T4, T5>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            var item4 = default(T4);
            var item5 = default(T5);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        item4 = _item4Codec.ReadValue(ref reader, header);
                        break;
                    case 5:
                        item5 = _item5Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }

    [RegisterCopier]
    public class TupleCopier<T1, T2, T3, T4, T5> : IDeepCopier<Tuple<T1, T2, T3, T4, T5>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3,
            IDeepCopier<T4> copier4,
            IDeepCopier<T5> copier5)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
            _copier4 = copier4;
            _copier5 = copier5;
        }

        public Tuple<T1, T2, T3, T4, T5> DeepCopy(Tuple<T1, T2, T3, T4, T5> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3, T4, T5> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3, T4, T5>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context),
                _copier4.DeepCopy(input.Item4, context),
                _copier5.DeepCopy(input.Item5, context));
            context.RecordCopy(input, result);
            return result;
        }
    } 

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3, T4, T5, T6> : IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);
        private static readonly Type ElementType4 = typeof(T4);
        private static readonly Type ElementType5 = typeof(T5);
        private static readonly Type ElementType6 = typeof(T6);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;
        private readonly IFieldCodec<T6> _item6Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec,
            IFieldCodec<T4> item4Codec,
            IFieldCodec<T5> item5Codec,
            IFieldCodec<T6> item6Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
            _item4Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item4Codec);
            _item5Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item5Codec);
            _item6Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item6Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            Tuple<T1, T2, T3, T4, T5, T6> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);
            _item4Codec.WriteField(ref writer, 1, ElementType4, value.Item4);
            _item5Codec.WriteField(ref writer, 1, ElementType5, value.Item5);
            _item6Codec.WriteField(ref writer, 1, ElementType6, value.Item6);

            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3, T4, T5, T6> IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3, T4, T5, T6>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            var item4 = default(T4);
            var item5 = default(T5);
            var item6 = default(T6);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        item4 = _item4Codec.ReadValue(ref reader, header);
                        break;
                    case 5:
                        item5 = _item5Codec.ReadValue(ref reader, header);
                        break;
                    case 6:
                        item6 = _item6Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2, T3, T4, T5, T6> : IDeepCopier<Tuple<T1, T2, T3, T4, T5, T6>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;
        private readonly IDeepCopier<T6> _copier6;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3,
            IDeepCopier<T4> copier4,
            IDeepCopier<T5> copier5,
            IDeepCopier<T6> copier6)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
            _copier4 = copier4;
            _copier5 = copier5;
            _copier6 = copier6;
        }

        public Tuple<T1, T2, T3, T4, T5, T6> DeepCopy(Tuple<T1, T2, T3, T4, T5, T6> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3, T4, T5, T6> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3, T4, T5, T6>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context),
                _copier4.DeepCopy(input.Item4, context),
                _copier5.DeepCopy(input.Item5, context),
                _copier6.DeepCopy(input.Item6, context));
            context.RecordCopy(input, result);
            return result;
        }
    } 

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3, T4, T5, T6, T7> : IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);
        private static readonly Type ElementType4 = typeof(T4);
        private static readonly Type ElementType5 = typeof(T5);
        private static readonly Type ElementType6 = typeof(T6);
        private static readonly Type ElementType7 = typeof(T7);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;
        private readonly IFieldCodec<T6> _item6Codec;
        private readonly IFieldCodec<T7> _item7Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec,
            IFieldCodec<T4> item4Codec,
            IFieldCodec<T5> item5Codec,
            IFieldCodec<T6> item6Codec,
            IFieldCodec<T7> item7Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
            _item4Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item4Codec);
            _item5Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item5Codec);
            _item6Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item6Codec);
            _item7Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item7Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            Tuple<T1, T2, T3, T4, T5, T6, T7> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);
            _item4Codec.WriteField(ref writer, 1, ElementType4, value.Item4);
            _item5Codec.WriteField(ref writer, 1, ElementType5, value.Item5);
            _item6Codec.WriteField(ref writer, 1, ElementType6, value.Item6);
            _item7Codec.WriteField(ref writer, 1, ElementType7, value.Item7);


            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3, T4, T5, T6, T7> IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3, T4, T5, T6, T7>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            var item4 = default(T4);
            var item5 = default(T5);
            var item6 = default(T6);
            var item7 = default(T7);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        item4 = _item4Codec.ReadValue(ref reader, header);
                        break;
                    case 5:
                        item5 = _item5Codec.ReadValue(ref reader, header);
                        break;
                    case 6:
                        item6 = _item6Codec.ReadValue(ref reader, header);
                        break;
                    case 7:
                        item7 = _item7Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2, T3, T4, T5, T6, T7> : IDeepCopier<Tuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;
        private readonly IDeepCopier<T6> _copier6;
        private readonly IDeepCopier<T7> _copier7;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3,
            IDeepCopier<T4> copier4,
            IDeepCopier<T5> copier5,
            IDeepCopier<T6> copier6,
            IDeepCopier<T7> copier7)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
            _copier4 = copier4;
            _copier5 = copier5;
            _copier6 = copier6;
            _copier7 = copier7;
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7> DeepCopy(Tuple<T1, T2, T3, T4, T5, T6, T7> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3, T4, T5, T6, T7> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3, T4, T5, T6, T7>))
            {
                return context.Copy(input);
            }

            result = Tuple.Create(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context),
                _copier4.DeepCopy(input.Item4, context),
                _copier5.DeepCopy(input.Item5, context),
                _copier6.DeepCopy(input.Item6, context),
                _copier7.DeepCopy(input.Item7, context));
            context.RecordCopy(input, result);
            return result;
        }
    } 

    [RegisterSerializer]
    public class TupleCodec<T1, T2, T3, T4, T5, T6, T7, T8> : IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7, T8>>
    {
        private static readonly Type ElementType1 = typeof(T1);
        private static readonly Type ElementType2 = typeof(T2);
        private static readonly Type ElementType3 = typeof(T3);
        private static readonly Type ElementType4 = typeof(T4);
        private static readonly Type ElementType5 = typeof(T5);
        private static readonly Type ElementType6 = typeof(T6);
        private static readonly Type ElementType7 = typeof(T7);
        private static readonly Type ElementType8 = typeof(T8);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;
        private readonly IFieldCodec<T6> _item6Codec;
        private readonly IFieldCodec<T7> _item7Codec;
        private readonly IFieldCodec<T8> _item8Codec;

        public TupleCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec,
            IFieldCodec<T4> item4Codec,
            IFieldCodec<T5> item5Codec,
            IFieldCodec<T6> item6Codec,
            IFieldCodec<T7> item7Codec,
            IFieldCodec<T8> item8Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
            _item4Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item4Codec);
            _item5Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item5Codec);
            _item6Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item6Codec);
            _item7Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item7Codec);
            _item8Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item8Codec);
        }

        void IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7, T8>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer,
            uint fieldIdDelta,
            Type expectedType,
            Tuple<T1, T2, T3, T4, T5, T6, T7, T8> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.TagDelimited);

            _item1Codec.WriteField(ref writer, 1, ElementType1, value.Item1);
            _item2Codec.WriteField(ref writer, 1, ElementType2, value.Item2);
            _item3Codec.WriteField(ref writer, 1, ElementType3, value.Item3);
            _item4Codec.WriteField(ref writer, 1, ElementType4, value.Item4);
            _item5Codec.WriteField(ref writer, 1, ElementType5, value.Item5);
            _item6Codec.WriteField(ref writer, 1, ElementType6, value.Item6);
            _item7Codec.WriteField(ref writer, 1, ElementType7, value.Item7);
            _item8Codec.WriteField(ref writer, 1, ElementType8, value.Rest);

            writer.WriteEndObject();
        }

        Tuple<T1, T2, T3, T4, T5, T6, T7, T8> IFieldCodec<Tuple<T1, T2, T3, T4, T5, T6, T7, T8>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<Tuple<T1, T2, T3, T4, T5, T6, T7, T8>, TInput>(ref reader, field);
            }

            if (field.WireType != WireType.TagDelimited)
            {
                ThrowUnsupportedWireTypeException();
            }

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var item1 = default(T1);
            var item2 = default(T2);
            var item3 = default(T3);
            var item4 = default(T4);
            var item5 = default(T5);
            var item6 = default(T6);
            var item7 = default(T7);
            var item8 = default(T8);
            uint fieldId = 0;
            while (true)
            {
                var header = reader.ReadFieldHeader();
                if (header.IsEndBaseOrEndObject)
                {
                    break;
                }

                fieldId += header.FieldIdDelta;
                switch (fieldId)
                {
                    case 1:
                        item1 = _item1Codec.ReadValue(ref reader, header);
                        break;
                    case 2:
                        item2 = _item2Codec.ReadValue(ref reader, header);
                        break;
                    case 3:
                        item3 = _item3Codec.ReadValue(ref reader, header);
                        break;
                    case 4:
                        item4 = _item4Codec.ReadValue(ref reader, header);
                        break;
                    case 5:
                        item5 = _item5Codec.ReadValue(ref reader, header);
                        break;
                    case 6:
                        item6 = _item6Codec.ReadValue(ref reader, header);
                        break;
                    case 7:
                        item7 = _item7Codec.ReadValue(ref reader, header);
                        break;
                    case 8:
                        item8 = _item8Codec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            var result = new Tuple<T1, T2, T3, T4, T5, T6, T7, T8>(item1, item2, item3, item4, item5, item6, item7, item8);
            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowUnsupportedWireTypeException() => throw new UnsupportedWireTypeException(
            $"Only a {nameof(WireType)} value of {WireType.TagDelimited} is supported for tuple fields.");
    }
    
    [RegisterCopier]
    public class TupleCopier<T1, T2, T3, T4, T5, T6, T7, T8> : IDeepCopier<Tuple<T1, T2, T3, T4, T5, T6, T7, T8>>
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;
        private readonly IDeepCopier<T6> _copier6;
        private readonly IDeepCopier<T7> _copier7;
        private readonly IDeepCopier<T8> _copier8;

        public TupleCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3,
            IDeepCopier<T4> copier4,
            IDeepCopier<T5> copier5,
            IDeepCopier<T6> copier6,
            IDeepCopier<T7> copier7,
            IDeepCopier<T8> copier8)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
            _copier4 = copier4;
            _copier5 = copier5;
            _copier6 = copier6;
            _copier7 = copier7;
            _copier8 = copier8;
        }

        public Tuple<T1, T2, T3, T4, T5, T6, T7, T8> DeepCopy(Tuple<T1, T2, T3, T4, T5, T6, T7, T8> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out Tuple<T1, T2, T3, T4, T5, T6, T7, T8> result))
            {
                return result;
            }

            if (input.GetType() != typeof(Tuple<T1, T2, T3, T4, T5, T6, T7, T8>))
            {
                return context.Copy(input);
            }

            result = new Tuple<T1, T2, T3, T4, T5, T6, T7, T8>(
                _copier1.DeepCopy(input.Item1, context),
                _copier2.DeepCopy(input.Item2, context),
                _copier3.DeepCopy(input.Item3, context),
                _copier4.DeepCopy(input.Item4, context),
                _copier5.DeepCopy(input.Item5, context),
                _copier6.DeepCopy(input.Item6, context),
                _copier7.DeepCopy(input.Item7, context),
                _copier8.DeepCopy(input.Rest, context));
            context.RecordCopy(input, result);
            return result;
        }
    } 
}
