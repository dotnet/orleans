using System;
using System.Buffers;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Cloning;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.WireProtocol;

namespace UnitTests.GrainInterfaces
{
    /// <summary>
    /// The ExceptionGrain interface.
    /// </summary>
    public interface IExceptionGrain : IGrainWithIntegerKey
    {
        Task Canceled();

        Task ThrowsInvalidOperationException();

        Task ThrowsNullReferenceException();

        Task ThrowsAggregateExceptionWrappingInvalidOperationException();

        Task ThrowsNestedAggregateExceptionsWrappingInvalidOperationException();

        Task GrainCallToThrowsInvalidOperationException(long otherGrainId);

        Task GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(long otherGrainId);

        Task ThrowsSynchronousInvalidOperationException();

        Task<object> ThrowsSynchronousExceptionObjectTask();

        Task ThrowsMultipleExceptionsAggregatedInFaultedTask();

        Task ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions();
    }

    public interface IMessageSerializationGrain : IGrainWithIntegerKey
    {
        Task SendUnserializable(UnserializableType input);
        Task SendUndeserializable(UndeserializableType input);
        Task<UnserializableType> GetUnserializable();
        Task<UndeserializableType> GetUndeserializable();

        Task SendUnserializableToOtherSilo();
        Task SendUndeserializableToOtherSilo();
        Task GetUnserializableFromOtherSilo();
        Task GetUndeserializableFromOtherSilo();

        Task SendUnserializableToClient(IMessageSerializationClientObject obj);
        Task SendUndeserializableToClient(IMessageSerializationClientObject obj);
        Task GetUnserializableFromClient(IMessageSerializationClientObject obj);
        Task GetUndeserializableFromClient(IMessageSerializationClientObject obj);

        Task<string> GetSiloIdentity();
    }

    public interface IMessageSerializationClientObject : IAddressable
    {
        Task SendUnserializable(UnserializableType input);
        Task SendUndeserializable(UndeserializableType input);
        Task<UnserializableType> GetUnserializable();
        Task<UndeserializableType> GetUndeserializable();
    }

    public struct UndeserializableType
    {
        public const string FailureMessage = "Can't do it, sorry.";

        public UndeserializableType(int num)
        {
            this.Number = num;
        }

        public int Number { get; }
    }

    [GenerateSerializer]
    public class UnserializableType
    {
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UndeserializableTypeCodec : IFieldCodec<UndeserializableType>, IDeepCopier<UndeserializableType>
    {
        public UndeserializableType DeepCopy(UndeserializableType input, CopyContext context) => input;

        public UndeserializableType ReadValue<TInput>(ref Reader<TInput> reader, Field field) => throw new NotSupportedException(UndeserializableType.FailureMessage);
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UndeserializableType value) where TBufferWriter : IBufferWriter<byte>
        {
            Int32Codec.WriteField(ref writer, fieldIdDelta, typeof(UndeserializableType), value.Number);
        }
    }

    [RegisterSerializer]
    [RegisterCopier]
    public sealed class UnserializableTypeCodec : IFieldCodec<UnserializableType>, IDeepCopier<UnserializableType>
    {
        public UnserializableType DeepCopy(UnserializableType input, CopyContext context) => input;

        public UnserializableType ReadValue<TInput>(ref Reader<TInput> reader, Field field) => default;
        public void WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, UnserializableType value) where TBufferWriter : IBufferWriter<byte>
        {
            throw new NotSupportedException(UndeserializableType.FailureMessage);
        }
    }
}