#nullable enable
using System;
using System.Buffers;
using System.Distributed.DurableTasks;
using Orleans.Serialization;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Runtime.DurableTasks;

/// <summary>
/// Activator for <see cref="PendingDurableTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class PendingDurableTaskResponseActivator : IActivator<PendingDurableTaskResponse>
{
    /// <inheritdoc/>
    public PendingDurableTaskResponse Create() => PendingDurableTaskResponse.Instance;
}

/// <summary>
/// Activator for <see cref="SubscribedDurableTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class SubscribedDurableTaskResponseActivator : IActivator<SubscribedDurableTaskResponse>
{
    /// <inheritdoc/>
    public SubscribedDurableTaskResponse Create() => SubscribedDurableTaskResponse.Instance;
}

/// <summary>
/// Activator for <see cref="SuccessDurableTaskResponse"/>.
/// </summary>
[RegisterActivator]
internal sealed class SuccessDurableTaskResponseActivator : IActivator<SuccessDurableTaskResponse>
{
    /// <inheritdoc/>
    public SuccessDurableTaskResponse Create() => SuccessDurableTaskResponse.Instance;
}

[RegisterSerializer, RegisterCopier]
internal sealed class PendingDurableTaskResponseCodec : IFieldCodec<PendingDurableTaskResponse>, IDeepCopier<PendingDurableTaskResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, PendingDurableTaskResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public PendingDurableTaskResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(PendingDurableTaskResponse), 0, length);

        return PendingDurableTaskResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object? DeepCopy(object? input, CopyContext context) => input;
    public PendingDurableTaskResponse DeepCopy(PendingDurableTaskResponse input, CopyContext context) => input;
}

[RegisterSerializer, RegisterCopier]
internal sealed class SubscribedDurableTaskResponseCodec : IFieldCodec<SubscribedDurableTaskResponse>, IDeepCopier<SubscribedDurableTaskResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, SubscribedDurableTaskResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public SubscribedDurableTaskResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(SubscribedDurableTaskResponse), 0, length);

        return SubscribedDurableTaskResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object? DeepCopy(object? input, CopyContext context) => input;
    public SubscribedDurableTaskResponse DeepCopy(SubscribedDurableTaskResponse input, CopyContext context) => input;
}

[RegisterSerializer, RegisterCopier]
internal sealed class SuccessDurableTaskResponseCodec : IFieldCodec<SuccessDurableTaskResponse>, IDeepCopier<SuccessDurableTaskResponse>, IOptionalDeepCopier
{
    /// <inheritdoc />
    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, SuccessDurableTaskResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeader(fieldIdDelta, expectedType, value.GetType(), WireType.VarInt);
        writer.WriteByte(1);
    }

    /// <inheritdoc />
    public SuccessDurableTaskResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        field.EnsureWireType(WireType.VarInt);

        ReferenceCodec.MarkValueField(reader.Session);
        var length = reader.ReadVarUInt32();
        if (length != 0) throw new UnexpectedLengthPrefixValueException(nameof(SuccessDurableTaskResponse), 0, length);

        return SuccessDurableTaskResponse.Instance;
    }

    public bool IsShallowCopyable() => true;
    public object? DeepCopy(object? input, CopyContext context) => input;
    public SuccessDurableTaskResponse DeepCopy(SuccessDurableTaskResponse input, CopyContext context) => input;
}

[RegisterSerializer]
internal sealed class DurableTaskResponseCodec<TResult> : IFieldCodec<DurableTaskResponse<TResult>>
{
    private readonly Type _codecFieldType = typeof(DurableTaskResponse<TResult>);
    private readonly Type _resultType = typeof(TResult);
    private readonly IFieldCodec<TResult> _codec;

    public DurableTaskResponseCodec(ICodecProvider codecProvider)
        => _codec = OrleansGeneratedCodeHelper.GetService<IFieldCodec<TResult>>(this, codecProvider);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, DurableTaskResponse<TResult> value) where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        if (value.TypedResult is not null)
        {
            _codec.WriteField(ref writer, 0, _resultType, value.TypedResult);
        }

        writer.WriteEndObject();
    }

    public DurableTaskResponse<TResult> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
            return ReferenceCodec.ReadReference<DurableTaskResponse<TResult>, TInput>(ref reader, field);

        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        reader.ReadFieldHeader(ref field);
        TResult? value;
        if (!field.IsEndBaseOrEndObject && field.FieldIdDelta == 0)
        {
            value = _codec.ReadValue(ref reader, field);
        }
        else
        {
            value = default;
        }

        var result = DurableTaskResponse.FromResult(value!);
        reader.ReadFieldHeader(ref field);
        reader.ConsumeEndBaseOrEndObject(ref field);

        return result;
    }
}

[RegisterCopier]
internal sealed class DurableTaskResponseCopier<TResult> : IDeepCopier<DurableTaskResponse<TResult>>
{
    private readonly IDeepCopier<TResult> _copier;

    public DurableTaskResponseCopier(ICodecProvider codecProvider)
        => _copier = OrleansGeneratedCodeHelper.GetService<IDeepCopier<TResult>>(this, codecProvider);

    public DurableTaskResponse<TResult> DeepCopy(DurableTaskResponse<TResult>? input, CopyContext context)
    {
        if (input is null)
            return null!;

        return DurableTaskResponse.FromResult(_copier.DeepCopy(input.TypedResult!, context));
    }
}

[RegisterSerializer]
internal sealed class ExceptionDurableTaskResponseCodec : IFieldCodec<ExceptionDurableTaskResponse>
{
    private readonly Type _codecFieldType = typeof(ExceptionDurableTaskResponse);
    private readonly IFieldCodec<Exception> _codec;

    public ExceptionDurableTaskResponseCodec(ICodecProvider codecProvider)
        => _codec = OrleansGeneratedCodeHelper.GetService<IFieldCodec<Exception>>(this, codecProvider);

    public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, ExceptionDurableTaskResponse value) where TBufferWriter : IBufferWriter<byte>
    {
        if (value is null)
        {
            ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
            return;
        }

        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
        if (value.Exception is not null)
        {
            _codec.WriteField(ref writer, 0, typeof(Exception), value.Exception);
        }

        writer.WriteEndObject();
    }

    public ExceptionDurableTaskResponse ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference)
            return ReferenceCodec.ReadReference<ExceptionDurableTaskResponse, TInput>(ref reader, field);

        field.EnsureWireTypeTagDelimited();
        ReferenceCodec.MarkValueField(reader.Session);
        reader.ReadFieldHeader(ref field);
        Exception? exception;
        if (!field.IsEndBaseOrEndObject && field.FieldIdDelta == 0)
        {
            exception = _codec.ReadValue(ref reader, field);
        }
        else
        {
            exception = default;
        }

        var result = new ExceptionDurableTaskResponse(exception!);
        reader.ReadFieldHeader(ref field);
        reader.ConsumeEndBaseOrEndObject(ref field);

        return result;
    }
}

[RegisterCopier]
internal sealed class ExceptionDurableTaskResponseCopier : IDeepCopier<ExceptionDurableTaskResponse>
{
    private readonly IDeepCopier<Exception> _copier;

    public ExceptionDurableTaskResponseCopier(ICodecProvider codecProvider)
        => _copier = OrleansGeneratedCodeHelper.GetService<IDeepCopier<Exception>>(this, codecProvider);

    public ExceptionDurableTaskResponse DeepCopy(ExceptionDurableTaskResponse? input, CopyContext context)
    {
        if (input is null)
            return null!;

        return new ExceptionDurableTaskResponse(_copier.DeepCopy(input.Exception, context));
    }
}

