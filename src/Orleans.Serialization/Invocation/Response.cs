using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Orleans.Serialization.Activators;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.GeneratedCodeHelpers;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.WireProtocol;

namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents the result of a method invocation.
    /// </summary>
    [SerializerTransparent]
    public abstract class Response : IDisposable
    {
        /// <summary>
        /// Creates a new response representing an exception.
        /// </summary>
        /// <param name="exception">The exception.</param>
        /// <returns>A new response.</returns>
        public static Response FromException(Exception exception) => new ExceptionResponse { Exception = exception };

        /// <summary>
        /// Creates a new response object which has been fulfilled with the provided value.
        /// </summary>
        /// <typeparam name="TResult">The underlying result type.</typeparam>
        /// <param name="value">The value.</param>
        /// <returns>A new response.</returns>
        public static Response FromResult<TResult>(TResult value)
        {
            var result = ResponsePool.Get<TResult>();
            result.TypedResult = value;
            return result;
        }

        /// <summary>
        /// Gets a completed response.
        /// </summary>
        public static Response Completed => CompletedResponse.Instance;

        /// <inheritdoc />
        public abstract object Result { get; set; }

        public virtual Type GetSimpleResultType() => null;

        /// <inheritdoc />
        public abstract Exception Exception { get; set; }

        /// <inheritdoc />
        public abstract T GetResult<T>();

        /// <inheritdoc />
        public abstract void Dispose();

        /// <inheritdoc />
        public override string ToString() => Exception is { } ex ? ex.ToString() : Result is { } r ? r.ToString() : "[null]";
    }

    /// <summary>
    /// Represents a completed <see cref="Response"/>.
    /// </summary>
    [GenerateSerializer, Immutable, UseActivator, SuppressReferenceTracking]
    public sealed class CompletedResponse : Response
    {
        /// <summary>
        /// Gets the singleton instance of this class.
        /// </summary>
        public static CompletedResponse Instance { get; } = new CompletedResponse();

        /// <inheritdoc/>
        public override object Result { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); } 

        /// <inheritdoc/>
        public override Exception Exception { get => null; set => throw new InvalidOperationException($"Type {nameof(CompletedResponse)} is read-only"); }

        /// <inheritdoc/>
        public override T GetResult<T>() => default;

        /// <inheritdoc/>
        public override void Dispose() { }

        /// <inheritdoc/>
        public override string ToString() => "[Completed]";
    }

    /// <summary>
    /// Activator for <see cref="CompletedResponse"/>.
    /// </summary>
    [RegisterActivator]
    internal sealed class CompletedResponseActivator : IActivator<CompletedResponse>
    {
        /// <inheritdoc/>
        public CompletedResponse Create() => CompletedResponse.Instance;
    }

    /// <summary>
    /// A <see cref="Response"/> which represents an exception, a broken promise.
    /// </summary>
    [GenerateSerializer, Immutable]
    public sealed class ExceptionResponse : Response
    {
        /// <inheritdoc/>
        public override object Result
        {
            get
            {
                ExceptionDispatchInfo.Capture(Exception).Throw();
                return null;
            }

            set => throw new InvalidOperationException($"Cannot set result property on response of type {nameof(ExceptionResponse)}");
        }

        /// <inheritdoc/>
        [Id(0)]
        public override Exception Exception { get; set; }

        /// <inheritdoc/>
        public override T GetResult<T>()
        {
            ExceptionDispatchInfo.Capture(Exception).Throw();
            return default;
        }

        /// <inheritdoc/>
        public override void Dispose() { }

        /// <inheritdoc/>
        public override string ToString() => Exception?.ToString() ?? "[null]";
    }

    /// <summary>
    /// A <see cref="Response"/> which represents a typed value.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    [UseActivator, SuppressReferenceTracking]
    public sealed class Response<TResult> : Response
    {
        [Id(0)]
        private TResult _result;

        public TResult TypedResult { get => _result; set => _result = value; }

        public override Exception Exception
        {
            get => null;
            set => throw new InvalidOperationException($"Cannot set {nameof(Exception)} property for type {nameof(Response<TResult>)}");
        }

        public override object Result
        {
            get => _result;
            set => _result = (TResult)value;
        }

        public override Type GetSimpleResultType() => typeof(TResult);

        public override T GetResult<T>()
        {
            if (typeof(TResult).IsValueType && typeof(T).IsValueType && typeof(T) == typeof(TResult))
                return Unsafe.As<TResult, T>(ref _result);

            return (T)(object)_result;
        }

        public override void Dispose()
        {
            _result = default;
            ResponsePool.Return(this);
        }

        public override string ToString() => _result is { } r ? r.ToString() : "[null]";
    }

    /// <summary>
    /// Supports raw serialization of <see cref="Response{TResult}"/> values.
    /// </summary>
    public abstract class ResponseCodec
    {
        public abstract void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, object value) where TBufferWriter : IBufferWriter<byte>;
        public abstract object ReadRaw<TInput>(ref Reader<TInput> reader, scoped ref Field field);
    }

    [RegisterSerializer]
    internal sealed class PooledResponseCodec<TResult> : ResponseCodec, IFieldCodec<Response<TResult>>
    {
        private readonly Type _codecFieldType = typeof(Response<TResult>);
        private readonly Type _resultType = typeof(TResult);
        private readonly IFieldCodec<TResult> _codec;

        public PooledResponseCodec(ICodecProvider codecProvider)
            => _codec = OrleansGeneratedCodeHelper.GetService<IFieldCodec<TResult>>(this, codecProvider);

        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, Response<TResult> value) where TBufferWriter : IBufferWriter<byte>
        {
            if (value is null)
            {
                ReferenceCodec.WriteNullReference(ref writer, fieldIdDelta);
                return;
            }

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteStartObject(fieldIdDelta, expectedType, _codecFieldType);
            if (value.TypedResult is not null)
                _codec.WriteField(ref writer, 0, _resultType, value.TypedResult);
            writer.WriteEndObject();
        }

        public Response<TResult> ReadValue<TInput>(ref Reader<TInput> reader, Field field)
        {
            if (field.IsReference)
                return ReferenceCodec.ReadReference<Response<TResult>, TInput>(ref reader, field);

            field.EnsureWireTypeTagDelimited();
            ReferenceCodec.MarkValueField(reader.Session);
            var result = ResponsePool.Get<TResult>();
            reader.ReadFieldHeader(ref field);
            if (!field.IsEndBaseOrEndObject)
            {
                result.TypedResult = _codec.ReadValue(ref reader, field);
                reader.ReadFieldHeader(ref field);
                reader.ConsumeEndBaseOrEndObject(ref field);
            }
            return result;
        }

        public override void WriteRaw<TBufferWriter>(ref Writer<TBufferWriter> writer, object value)
        {
            writer.WriteStartObject(0, null, _resultType);
            var holder = (Response<TResult>)value;
            if (holder.TypedResult is not null)
                _codec.WriteField(ref writer, 0, _resultType, holder.TypedResult);
            writer.WriteEndObject();
        }

        public override object ReadRaw<TInput>(ref Reader<TInput> reader, scoped ref Field field)
        {
            field.EnsureWireTypeTagDelimited();
            var result = ResponsePool.Get<TResult>();
            reader.ReadFieldHeader(ref field);
            if (!field.IsEndBaseOrEndObject)
            {
                result.TypedResult = _codec.ReadValue(ref reader, field);
                reader.ReadFieldHeader(ref field);
                reader.ConsumeEndBaseOrEndObject(ref field);
            }
            return result;
        }
    }

    [RegisterCopier]
    internal sealed class PooledResponseCopier<TResult> : IDeepCopier<Response<TResult>>
    {
        private readonly IDeepCopier<TResult> _copier;

        public PooledResponseCopier(ICodecProvider codecProvider)
            => _copier = OrleansGeneratedCodeHelper.GetService<IDeepCopier<TResult>>(this, codecProvider);

        public Response<TResult> DeepCopy(Response<TResult> input, CopyContext context)
        {
            if (input is null)
                return null;

            var result = ResponsePool.Get<TResult>();
            result.TypedResult = _copier.DeepCopy(input.TypedResult, context);
            return result;
        }
    }

    [RegisterActivator]
    internal sealed class PooledResponseActivator<TResult> : IActivator<Response<TResult>>
    {
        public Response<TResult> Create() => ResponsePool.Get<TResult>();
    }
}