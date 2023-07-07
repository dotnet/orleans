using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization
{
    /// <summary>
    /// Serializer for <see cref="FSharpOption{T}"/>.
    /// </summary>
    /// <typeparam name="T">The underlying type.</typeparam>
    [RegisterSerializer]
    public sealed class FSharpOptionCodec<T> : IFieldCodec<FSharpOption<T>>
    {
        private readonly Type CodecType = typeof(FSharpOption<T>);
        private readonly Type CodecFieldType = typeof(T);
        private readonly IFieldCodec<T> _fieldCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpOptionCodec{T}"/> class.
        /// </summary>
        public FSharpOptionCodec(IFieldCodec<T> fieldCodec)
        {
            _fieldCodec = OrleansGeneratedCodeHelper.UnwrapService(this, fieldCodec);
        }

        /// <inheritdoc/>
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpOption<T> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
                return;

            writer.WriteStartObject(fieldIdDelta, expectedType, CodecType);
            if (FSharpOption<T>.get_IsSome(value))
            {
                _fieldCodec.WriteField(ref writer, 0, CodecFieldType, value.Value);
            }
            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        public FSharpOption<T> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
                return ReferenceCodec.ReadReference<FSharpOption<T>, TInput>(ref reader, field);

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            var result = FSharpOption<T>.None;
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
                    case 0:
                        result = _fieldCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }

    /// <summary>
    /// Copier implementation for <see cref="FSharpOption{T}"/>.
    /// </summary>
    /// <typeparam name="T">The underlying value type of the option type.</typeparam>
    [RegisterCopier]
    public sealed class FSharpOptionCopier<T> : IDeepCopier<FSharpOption<T>>
    {
        private readonly IDeepCopier<T> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpOptionCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public FSharpOptionCopier(IDeepCopier<T> valueCopier)
        {
            _valueCopier = OrleansGeneratedCodeHelper.UnwrapService(this, valueCopier);
        }

        /// <inheritdoc/>
        public FSharpOption<T> DeepCopy(FSharpOption<T> input, CopyContext context)
        {
            if (input is null || FSharpOption<T>.get_IsNone(input))
            {
                return input;
            }

            if (context.TryGetCopy<FSharpOption<T>>(input, out var result))
            {
                return result;
            }

            result = _valueCopier.DeepCopy(input.Value, context);
            context.RecordCopy(input, result);
            return result;
        }
    }

    /// <summary>
    /// Serializer for <see cref="FSharpValueOption{T}"/>.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [RegisterSerializer]
    public class FSharpValueOptionCodec<T> : IFieldCodec<FSharpValueOption<T>>
    {
        private readonly IFieldCodec<T> _valueCodec;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpValueOptionCodec{T}"/> class.
        /// </summary>
        /// <param name="item1Codec">The item codec.</param>
        public FSharpValueOptionCodec(IFieldCodec<T> item1Codec)
        {
            _valueCodec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
        }

        /// <inheritdoc/>
        void IFieldCodec<FSharpValueOption<T>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpValueOption<T> value)
        {
            ReferenceCodec.MarkValueField(writer.Session);

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpValueOption<T>), WireType.TagDelimited);
            if (value.IsSome)
            {
                _valueCodec.WriteField(ref writer, 0, typeof(T), value.Value);
            }

            writer.WriteEndObject();
        }

        /// <inheritdoc/>
        FSharpValueOption<T> IFieldCodec<FSharpValueOption<T>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            var result = FSharpValueOption<T>.None;
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
                    case 0:
                        result = _valueCodec.ReadValue(ref reader, header);
                        break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Copier for <see cref="FSharpValueOption{T}"/>.
    /// </summary>
    /// <typeparam name="T">The underlying value type.</typeparam>
    [RegisterCopier]
    public sealed class FSharpValueOptionCopier<T> : IDeepCopier<FSharpValueOption<T>>
    {
        private readonly IDeepCopier<T> _valueCopier;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpValueOptionCopier{T}"/> class.
        /// </summary>
        /// <param name="valueCopier">The value copier.</param>
        public FSharpValueOptionCopier(IDeepCopier<T> valueCopier)
        {
            _valueCopier = OrleansGeneratedCodeHelper.UnwrapService(this, valueCopier);
        }

        /// <inheritdoc/>
        public FSharpValueOption<T> DeepCopy(FSharpValueOption<T> input, CopyContext context)
        {
            if (input.IsNone)
            {
                return input;
            }
            else
            {
                return FSharpValueOption<T>.Some(_valueCopier.DeepCopy(input.Value, context));
            }
        }
    }

    /// <summary>
    /// Serializer for <see cref="FSharpChoice{T1, T2}"/>.
    /// </summary>
    [RegisterSerializer]
    public class FSharpChoiceCodec<T1, T2> : IFieldCodec<FSharpChoice<T1, T2>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T1);
        private readonly Type ElementType2 = typeof(T2);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;

        /// <summary>
        /// Initializes a new instance of the <see cref="FSharpChoiceCodec{T1, T2}"/> class.
        /// </summary>
        /// <param name="item1Codec">The codec for <typeparamref name="T1"/>.</param>
        /// <param name="item2Codec">The codec for <typeparamref name="T2"/>.</param>
        public FSharpChoiceCodec(IFieldCodec<T1> item1Codec, IFieldCodec<T2> item2Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
        }

        /// <inheritdoc/>
        void IFieldCodec<FSharpChoice<T1, T2>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpChoice<T1, T2> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpChoice<T1, T2>), WireType.TagDelimited);

            switch (value)
            {
                case FSharpChoice<T1, T2>.Choice1Of2 c1: _item1Codec.WriteField(ref writer, 1, ElementType1, c1.Item); break;
                case FSharpChoice<T1, T2>.Choice2Of2 c2: _item2Codec.WriteField(ref writer, 2, ElementType2, c2.Item); break;
            }

            writer.WriteEndObject();
        }

        FSharpChoice<T1, T2> IFieldCodec<FSharpChoice<T1, T2>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<FSharpChoice<T1, T2>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            FSharpChoice<T1, T2> result = default;
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
                    case 1: result = FSharpChoice<T1, T2>.NewChoice1Of2(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpChoice<T1, T2>.NewChoice2Of2(_item2Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpChoiceCopier<T1, T2> : IDeepCopier<FSharpChoice<T1, T2>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;

        public FSharpChoiceCopier(IDeepCopier<T1> copier1, IDeepCopier<T2> copier2)
        {
            _copier1 = copier1;
            _copier2 = copier2;
        }

        public FSharpChoice<T1, T2> DeepCopy(FSharpChoice<T1, T2> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out FSharpChoice<T1, T2> result))
            {
                return result;
            }

            result = input switch
            {
                FSharpChoice<T1, T2>.Choice1Of2 c1 => FSharpChoice<T1, T2>.NewChoice1Of2(_copier1.DeepCopy(c1.Item, context)),
                FSharpChoice<T1, T2>.Choice2Of2 c2 => FSharpChoice<T1, T2>.NewChoice2Of2(_copier2.DeepCopy(c2.Item, context)),
                _ => throw new NotSupportedException($"Type {input.GetType()} is not supported"),
            };
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpChoiceCodec<T1, T2, T3> : IFieldCodec<FSharpChoice<T1, T2, T3>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T1);
        private readonly Type ElementType2 = typeof(T2);
        private readonly Type ElementType3 = typeof(T3);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;

        public FSharpChoiceCodec(
            IFieldCodec<T1> item1Codec,
            IFieldCodec<T2> item2Codec,
            IFieldCodec<T3> item3Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
            _item3Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item3Codec);
        }

        void IFieldCodec<FSharpChoice<T1, T2, T3>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpChoice<T1, T2, T3> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpChoice<T1, T2, T3>), WireType.TagDelimited);

            switch (value)
            {
                case FSharpChoice<T1, T2, T3>.Choice1Of3 c1: _item1Codec.WriteField(ref writer, 1, ElementType1, c1.Item); break;
                case FSharpChoice<T1, T2, T3>.Choice2Of3 c2: _item2Codec.WriteField(ref writer, 2, ElementType2, c2.Item); break;
                case FSharpChoice<T1, T2, T3>.Choice3Of3 c3: _item3Codec.WriteField(ref writer, 3, ElementType3, c3.Item); break;
            }

            writer.WriteEndObject();
        }

        FSharpChoice<T1, T2, T3> IFieldCodec<FSharpChoice<T1, T2, T3>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<FSharpChoice<T1, T2, T3>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            FSharpChoice<T1, T2, T3> result = default;
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
                    case 1: result = FSharpChoice<T1, T2, T3>.NewChoice1Of3(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpChoice<T1, T2, T3>.NewChoice2Of3(_item2Codec.ReadValue(ref reader, header)); break;
                    case 3: result = FSharpChoice<T1, T2, T3>.NewChoice3Of3(_item3Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpChoiceCopier<T1, T2, T3> : IDeepCopier<FSharpChoice<T1, T2, T3>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;

        public FSharpChoiceCopier(
            IDeepCopier<T1> copier1,
            IDeepCopier<T2> copier2,
            IDeepCopier<T3> copier3)
        {
            _copier1 = copier1;
            _copier2 = copier2;
            _copier3 = copier3;
        }

        public FSharpChoice<T1, T2, T3> DeepCopy(FSharpChoice<T1, T2, T3> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out FSharpChoice<T1, T2, T3> result))
            {
                return result;
            }

            result = input switch
            {
                FSharpChoice<T1, T2, T3>.Choice1Of3 c1 => FSharpChoice<T1, T2, T3>.NewChoice1Of3(_copier1.DeepCopy(c1.Item, context)),
                FSharpChoice<T1, T2, T3>.Choice2Of3 c2 => FSharpChoice<T1, T2, T3>.NewChoice2Of3(_copier2.DeepCopy(c2.Item, context)),
                FSharpChoice<T1, T2, T3>.Choice3Of3 c3 => FSharpChoice<T1, T2, T3>.NewChoice3Of3(_copier3.DeepCopy(c3.Item, context)),
                _ => throw new NotSupportedException($"Type {input.GetType()} is not supported"),
            };
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpChoiceCodec<T1, T2, T3, T4> : IFieldCodec<FSharpChoice<T1, T2, T3, T4>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T1);
        private readonly Type ElementType2 = typeof(T2);
        private readonly Type ElementType3 = typeof(T3);
        private readonly Type ElementType4 = typeof(T4);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;

        public FSharpChoiceCodec(
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

        void IFieldCodec<FSharpChoice<T1, T2, T3, T4>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpChoice<T1, T2, T3, T4> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpChoice<T1, T2, T3, T4>), WireType.TagDelimited);

            switch (value)
            {
                case FSharpChoice<T1, T2, T3, T4>.Choice1Of4 c1: _item1Codec.WriteField(ref writer, 1, ElementType1, c1.Item); break;
                case FSharpChoice<T1, T2, T3, T4>.Choice2Of4 c2: _item2Codec.WriteField(ref writer, 2, ElementType2, c2.Item); break;
                case FSharpChoice<T1, T2, T3, T4>.Choice3Of4 c3: _item3Codec.WriteField(ref writer, 3, ElementType3, c3.Item); break;
                case FSharpChoice<T1, T2, T3, T4>.Choice4Of4 c4: _item4Codec.WriteField(ref writer, 4, ElementType4, c4.Item); break;
            }

            writer.WriteEndObject();
        }

        FSharpChoice<T1, T2, T3, T4> IFieldCodec<FSharpChoice<T1, T2, T3, T4>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<FSharpChoice<T1, T2, T3, T4>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            FSharpChoice<T1, T2, T3, T4> result = default;
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
                    case 1: result = FSharpChoice<T1, T2, T3, T4>.NewChoice1Of4(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpChoice<T1, T2, T3, T4>.NewChoice2Of4(_item2Codec.ReadValue(ref reader, header)); break;
                    case 3: result = FSharpChoice<T1, T2, T3, T4>.NewChoice3Of4(_item3Codec.ReadValue(ref reader, header)); break;
                    case 4: result = FSharpChoice<T1, T2, T3, T4>.NewChoice4Of4(_item4Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpChoiceCopier<T1, T2, T3, T4> : IDeepCopier<FSharpChoice<T1, T2, T3, T4>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;

        public FSharpChoiceCopier(
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

        public FSharpChoice<T1, T2, T3, T4> DeepCopy(FSharpChoice<T1, T2, T3, T4> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out FSharpChoice<T1, T2, T3, T4> result))
            {
                return result;
            }

            result = input switch
            {
                FSharpChoice<T1, T2, T3, T4>.Choice1Of4 c1 => FSharpChoice<T1, T2, T3, T4>.NewChoice1Of4(_copier1.DeepCopy(c1.Item, context)),
                FSharpChoice<T1, T2, T3, T4>.Choice2Of4 c2 => FSharpChoice<T1, T2, T3, T4>.NewChoice2Of4(_copier2.DeepCopy(c2.Item, context)),
                FSharpChoice<T1, T2, T3, T4>.Choice3Of4 c3 => FSharpChoice<T1, T2, T3, T4>.NewChoice3Of4(_copier3.DeepCopy(c3.Item, context)),
                FSharpChoice<T1, T2, T3, T4>.Choice4Of4 c4 => FSharpChoice<T1, T2, T3, T4>.NewChoice4Of4(_copier4.DeepCopy(c4.Item, context)),
                _ => throw new NotSupportedException($"Type {input.GetType()} is not supported"),
            };
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpChoiceCodec<T1, T2, T3, T4, T5> : IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T1);
        private readonly Type ElementType2 = typeof(T2);
        private readonly Type ElementType3 = typeof(T3);
        private readonly Type ElementType4 = typeof(T4);
        private readonly Type ElementType5 = typeof(T5);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;

        public FSharpChoiceCodec(
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

        void IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpChoice<T1, T2, T3, T4, T5> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpChoice<T1, T2, T3, T4, T5>), WireType.TagDelimited);

            switch (value)
            {
                case FSharpChoice<T1, T2, T3, T4, T5>.Choice1Of5 c1: _item1Codec.WriteField(ref writer, 1, ElementType1, c1.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5>.Choice2Of5 c2: _item2Codec.WriteField(ref writer, 2, ElementType2, c2.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5>.Choice3Of5 c3: _item3Codec.WriteField(ref writer, 3, ElementType3, c3.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5>.Choice4Of5 c4: _item4Codec.WriteField(ref writer, 4, ElementType4, c4.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5>.Choice5Of5 c5: _item5Codec.WriteField(ref writer, 5, ElementType5, c5.Item); break;
            }

            writer.WriteEndObject();
        }

        FSharpChoice<T1, T2, T3, T4, T5> IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<FSharpChoice<T1, T2, T3, T4, T5>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            FSharpChoice<T1, T2, T3, T4, T5> result = default;
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
                    case 1: result = FSharpChoice<T1, T2, T3, T4, T5>.NewChoice1Of5(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpChoice<T1, T2, T3, T4, T5>.NewChoice2Of5(_item2Codec.ReadValue(ref reader, header)); break;
                    case 3: result = FSharpChoice<T1, T2, T3, T4, T5>.NewChoice3Of5(_item3Codec.ReadValue(ref reader, header)); break;
                    case 4: result = FSharpChoice<T1, T2, T3, T4, T5>.NewChoice4Of5(_item4Codec.ReadValue(ref reader, header)); break;
                    case 5: result = FSharpChoice<T1, T2, T3, T4, T5>.NewChoice5Of5(_item5Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpChoiceCopier<T1, T2, T3, T4, T5> : IDeepCopier<FSharpChoice<T1, T2, T3, T4, T5>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;

        public FSharpChoiceCopier(
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

        public FSharpChoice<T1, T2, T3, T4, T5> DeepCopy(FSharpChoice<T1, T2, T3, T4, T5> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out FSharpChoice<T1, T2, T3, T4, T5> result))
            {
                return result;
            }

            result = input switch
            {
                FSharpChoice<T1, T2, T3, T4, T5>.Choice1Of5 c1 => FSharpChoice<T1, T2, T3, T4, T5>.NewChoice1Of5(_copier1.DeepCopy(c1.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5>.Choice2Of5 c2 => FSharpChoice<T1, T2, T3, T4, T5>.NewChoice2Of5(_copier2.DeepCopy(c2.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5>.Choice3Of5 c3 => FSharpChoice<T1, T2, T3, T4, T5>.NewChoice3Of5(_copier3.DeepCopy(c3.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5>.Choice4Of5 c4 => FSharpChoice<T1, T2, T3, T4, T5>.NewChoice4Of5(_copier4.DeepCopy(c4.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5>.Choice5Of5 c5 => FSharpChoice<T1, T2, T3, T4, T5>.NewChoice5Of5(_copier5.DeepCopy(c5.Item, context)),
                _ => throw new NotSupportedException($"Type {input.GetType()} is not supported"),
            };
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpChoiceCodec<T1, T2, T3, T4, T5, T6> : IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5, T6>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T1);
        private readonly Type ElementType2 = typeof(T2);
        private readonly Type ElementType3 = typeof(T3);
        private readonly Type ElementType4 = typeof(T4);
        private readonly Type ElementType5 = typeof(T5);
        private readonly Type ElementType6 = typeof(T6);

        private readonly IFieldCodec<T1> _item1Codec;
        private readonly IFieldCodec<T2> _item2Codec;
        private readonly IFieldCodec<T3> _item3Codec;
        private readonly IFieldCodec<T4> _item4Codec;
        private readonly IFieldCodec<T5> _item5Codec;
        private readonly IFieldCodec<T6> _item6Codec;

        public FSharpChoiceCodec(
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

        void IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5, T6>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpChoice<T1, T2, T3, T4, T5, T6> value)
        {
            if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value))
            {
                return;
            }

            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpChoice<T1, T2, T3, T4, T5, T6>), WireType.TagDelimited);

            switch (value)
            {
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice1Of6 c1: _item1Codec.WriteField(ref writer, 1, ElementType1, c1.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice2Of6 c2: _item2Codec.WriteField(ref writer, 2, ElementType2, c2.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice3Of6 c3: _item3Codec.WriteField(ref writer, 3, ElementType3, c3.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice4Of6 c4: _item4Codec.WriteField(ref writer, 4, ElementType4, c4.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice5Of6 c5: _item5Codec.WriteField(ref writer, 5, ElementType5, c5.Item); break;
                case FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice6Of6 c6: _item6Codec.WriteField(ref writer, 6, ElementType6, c6.Item); break;
            }

            writer.WriteEndObject();
        }

        FSharpChoice<T1, T2, T3, T4, T5, T6> IFieldCodec<FSharpChoice<T1, T2, T3, T4, T5, T6>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.WireType == WireType.Reference)
            {
                return ReferenceCodec.ReadReference<FSharpChoice<T1, T2, T3, T4, T5, T6>, TInput>(ref reader, field);
            }

            field.EnsureWireTypeTagDelimited();

            var placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
            FSharpChoice<T1, T2, T3, T4, T5, T6> result = default;
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
                    case 1: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice1Of6(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice2Of6(_item2Codec.ReadValue(ref reader, header)); break;
                    case 3: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice3Of6(_item3Codec.ReadValue(ref reader, header)); break;
                    case 4: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice4Of6(_item4Codec.ReadValue(ref reader, header)); break;
                    case 5: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice5Of6(_item5Codec.ReadValue(ref reader, header)); break;
                    case 6: result = FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice6Of6(_item6Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpChoiceCopier<T1, T2, T3, T4, T5, T6> : IDeepCopier<FSharpChoice<T1, T2, T3, T4, T5, T6>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T1> _copier1;
        private readonly IDeepCopier<T2> _copier2;
        private readonly IDeepCopier<T3> _copier3;
        private readonly IDeepCopier<T4> _copier4;
        private readonly IDeepCopier<T5> _copier5;
        private readonly IDeepCopier<T6> _copier6;

        public FSharpChoiceCopier(
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

        public FSharpChoice<T1, T2, T3, T4, T5, T6> DeepCopy(FSharpChoice<T1, T2, T3, T4, T5, T6> input, CopyContext context)
        {
            if (context.TryGetCopy(input, out FSharpChoice<T1, T2, T3, T4, T5, T6> result))
            {
                return result;
            }

            result = input switch
            {
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice1Of6 c1 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice1Of6(_copier1.DeepCopy(c1.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice2Of6 c2 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice2Of6(_copier2.DeepCopy(c2.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice3Of6 c3 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice3Of6(_copier3.DeepCopy(c3.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice4Of6 c4 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice4Of6(_copier4.DeepCopy(c4.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice5Of6 c5 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice5Of6(_copier5.DeepCopy(c5.Item, context)),
                FSharpChoice<T1, T2, T3, T4, T5, T6>.Choice6Of6 c6 => FSharpChoice<T1, T2, T3, T4, T5, T6>.NewChoice6Of6(_copier6.DeepCopy(c6.Item, context)),
                _ => throw new NotSupportedException($"Type {input.GetType()} is not supported"),
            };
            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpRefCodec<T> : GeneralizedReferenceTypeSurrogateCodec<FSharpRef<T>, FSharpRefSurrogate<T>>
    {
        public FSharpRefCodec(IValueSerializer<FSharpRefSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override FSharpRef<T> ConvertFromSurrogate(ref FSharpRefSurrogate<T> surrogate)
        {
            return new FSharpRef<T>(surrogate.Value);
        }

        public override void ConvertToSurrogate(FSharpRef<T> value, ref FSharpRefSurrogate<T> surrogate)
        {
            surrogate.Value = value.Value;
        }
    }

    [GenerateSerializer]
    public struct FSharpRefSurrogate<T>
    {
        [Id(0)]
        public T Value { get; set; }
    }

    [RegisterCopier]
    public class FSharpRefCopier<T> : IDeepCopier<FSharpRef<T>>
    {
        private readonly IDeepCopier<T> _copier;
        public FSharpRefCopier(IDeepCopier<T> copier) => _copier = copier;
        public FSharpRef<T> DeepCopy(FSharpRef<T> input, CopyContext context)
        {
            if (context.TryGetCopy<FSharpRef<T>>(input, out var result))
            {
                return result;
            }

            result = input switch
            {
                not null => new FSharpRef<T>(_copier.DeepCopy(input.Value, context)),
                null => null
            };

            context.RecordCopy(input, result);
            return result;
        }
    }

    [RegisterSerializer]
    public class FSharpListCodec<T> : GeneralizedReferenceTypeSurrogateCodec<FSharpList<T>, FSharpListSurrogate<T>>
    {
        public FSharpListCodec(IValueSerializer<FSharpListSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override FSharpList<T> ConvertFromSurrogate(ref FSharpListSurrogate<T> surrogate)
        {
            if (surrogate.Value is null) return null;

            return ListModule.OfSeq(surrogate.Value);
        }

        public override void ConvertToSurrogate(FSharpList<T> value, ref FSharpListSurrogate<T> surrogate)
        {
            if (value is null) return;

            surrogate.Value = new(ListModule.ToSeq(value));
        }
    }

    [GenerateSerializer]
    public struct FSharpListSurrogate<T>
    {
        [Id(0)]
        public List<T> Value { get; set; }
    }

    [RegisterCopier]
    public class FSharpListCopier<T> : IDeepCopier<FSharpList<T>>
    {
        private readonly IDeepCopier<T> _copier;
        public FSharpListCopier(IDeepCopier<T> copier) => _copier = copier;

        public FSharpList<T> DeepCopy(FSharpList<T> input, CopyContext context)
        {
            if (context.TryGetCopy<FSharpList<T>>(input, out var result))
            {
                return result;
            }

            result = ListModule.OfSeq(CopyElements(input, context));
            context.RecordCopy(input, result);
            return result;

            IEnumerable<T> CopyElements(FSharpList<T> list, CopyContext context)
            {
                foreach (var element in list)
                {
                    yield return _copier.DeepCopy(element, context);
                }
            }
        }
    }

    [RegisterSerializer]
    public class FSharpSetCodec<T> : GeneralizedReferenceTypeSurrogateCodec<FSharpSet<T>, FSharpSetSurrogate<T>>
    {
        public FSharpSetCodec(IValueSerializer<FSharpSetSurrogate<T>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override FSharpSet<T> ConvertFromSurrogate(ref FSharpSetSurrogate<T> surrogate)
        {
            if (surrogate.Value is null) return null;
            return new FSharpSet<T>(surrogate.Value);
        }

        public override void ConvertToSurrogate(FSharpSet<T> value, ref FSharpSetSurrogate<T> surrogate)
        {
            if (value is null) return;
            surrogate.Value = value.ToList();
        }
    }

    [GenerateSerializer]
    public struct FSharpSetSurrogate<T>
    {
        [Id(0)]
        public List<T> Value { get; set; }
    }

    [RegisterCopier]
    public class FSharpSetCopier<T> : IDeepCopier<FSharpSet<T>>
    {
        private readonly IDeepCopier<T> _copier;
        public FSharpSetCopier(IDeepCopier<T> copier) => _copier = copier;

        public FSharpSet<T> DeepCopy(FSharpSet<T> input, CopyContext context)
        {
            if (context.TryGetCopy<FSharpSet<T>>(input, out var result))
            {
                return result;
            }

            result = SetModule.OfSeq(CopyElements(input, context));
            context.RecordCopy(input, result);
            return result;

            IEnumerable<T> CopyElements(FSharpSet<T> vals, CopyContext context)
            {
                foreach (var element in vals)
                {
                    yield return _copier.DeepCopy(element, context);
                }
            }
        }
    }

    [RegisterSerializer]
    public class FSharpMapCodec<TKey, TValue> : GeneralizedReferenceTypeSurrogateCodec<FSharpMap<TKey, TValue>, FSharpMapSurrogate<TKey, TValue>>
    {
        public FSharpMapCodec(IValueSerializer<FSharpMapSurrogate<TKey, TValue>> surrogateSerializer) : base(surrogateSerializer)
        {
        }

        public override FSharpMap<TKey, TValue> ConvertFromSurrogate(ref FSharpMapSurrogate<TKey, TValue> surrogate)
        {
            if (surrogate.Value is null) return null;

            return new FSharpMap<TKey, TValue>(surrogate.Value);
        }

        public override void ConvertToSurrogate(FSharpMap<TKey, TValue> value, ref FSharpMapSurrogate<TKey, TValue> surrogate)
        {
            if (value is null) return;

            surrogate.Value = new(value.Count);
            surrogate.Value.AddRange(MapModule.ToSeq(value));
        }
    }

    [GenerateSerializer]
    public struct FSharpMapSurrogate<TKey, TValue>
    {
        [Id(0)]
        public List<Tuple<TKey, TValue>> Value { get; set; }
    }

    [RegisterCopier]
    public class FSharpMapCopier<TKey, TValue> : IDeepCopier<FSharpMap<TKey, TValue>>
    {
        private readonly IDeepCopier<TKey> _keyCopier;
        private readonly IDeepCopier<TValue> _valueCopier;

        public FSharpMapCopier(IDeepCopier<TKey> keyCopier, IDeepCopier<TValue> valueCopier)
        {
            _keyCopier = keyCopier;
            _valueCopier = valueCopier;
        }

        public FSharpMap<TKey, TValue> DeepCopy(FSharpMap<TKey, TValue> input, CopyContext context)
        {
            if (context.TryGetCopy<FSharpMap<TKey, TValue>>(input, out var result))
            {
                return result;
            }

            result = MapModule.OfSeq(CopyElements(input, context));
            context.RecordCopy(input, result);
            return result;

            IEnumerable<Tuple<TKey, TValue>> CopyElements(FSharpMap<TKey, TValue> vals, CopyContext context)
            {
                foreach (var element in vals)
                {
                    yield return Tuple.Create(_keyCopier.DeepCopy(element.Key, context), _valueCopier.DeepCopy(element.Value, context));
                }
            }
        }
    }

    [RegisterSerializer]
    public class FSharpResultCodec<T, TError> : IFieldCodec<FSharpResult<T, TError>>, IDerivedTypeCodec
    {
        private readonly Type ElementType1 = typeof(T);
        private readonly Type ElementType2 = typeof(TError);

        private readonly IFieldCodec<T> _item1Codec;
        private readonly IFieldCodec<TError> _item2Codec;

        public FSharpResultCodec(IFieldCodec<T> item1Codec, IFieldCodec<TError> item2Codec)
        {
            _item1Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item1Codec);
            _item2Codec = OrleansGeneratedCodeHelper.UnwrapService(this, item2Codec);
        }

        void IFieldCodec<FSharpResult<T, TError>>.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, FSharpResult<T, TError> value)
        {
            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeader(fieldIdDelta, expectedType, typeof(FSharpResult<T, TError>), WireType.TagDelimited);

            if (value.IsError)
            {
                _item2Codec.WriteField(ref writer, 2, ElementType2, value.ErrorValue);
            }
            else
            {
                _item1Codec.WriteField(ref writer, 1, ElementType1, value.ResultValue);
            }

            writer.WriteEndObject();
        }

        FSharpResult<T, TError> IFieldCodec<FSharpResult<T, TError>>.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            FSharpResult<T, TError> result = default;
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
                    case 1: result = FSharpResult<T, TError>.NewOk(_item1Codec.ReadValue(ref reader, header)); break;
                    case 2: result = FSharpResult<T, TError>.NewError(_item2Codec.ReadValue(ref reader, header)); break;
                    default:
                        reader.ConsumeUnknownField(header);
                        break;
                }
            }

            return result;
        }
    }
    
    [RegisterCopier]
    public class FSharpResultCopier<T, TError> : IDeepCopier<FSharpResult<T, TError>>, IDerivedTypeCopier
    {
        private readonly IDeepCopier<T> _copier1;
        private readonly IDeepCopier<TError> _copier2;

        public FSharpResultCopier(IDeepCopier<T> copier1, IDeepCopier<TError> copier2)
        {
            _copier1 = copier1;
            _copier2 = copier2;
        }

        public FSharpResult<T, TError> DeepCopy(FSharpResult<T, TError> input, CopyContext context)
        {
            if (input.IsError)
            {
                return FSharpResult<T, TError>.NewError(_copier2.DeepCopy(input.ErrorValue, context));
            }
            else
            {
                return FSharpResult<T, TError>.NewOk(_copier1.DeepCopy(input.ResultValue, context));
            }
        }
    }

}