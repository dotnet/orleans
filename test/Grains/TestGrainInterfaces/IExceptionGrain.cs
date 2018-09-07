using System;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.Serialization;

namespace UnitTests.GrainInterfaces
{
    using System.Threading.Tasks;
    using Orleans;

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
        Task Send(UndeserializableType input);
        Task<UnserializableType> Get();

        Task SendToOtherSilo();
        Task GetFromOtheSilo();

        Task SendToClient(IMessageSerializationClientObject obj);
        Task GetFromClient(IMessageSerializationClientObject obj);

        Task<string> GetSiloIdentity();
    }

    public interface IMessageSerializationClientObject : IAddressable
    {
        Task Send(UndeserializableType input);
        Task<UnserializableType> Get();
    }

    [Serializable]
    public struct UndeserializableType
    {
        public const string FailureMessage = "Can't do it, sorry.";

        public UndeserializableType(int num)
        {
            this.Number = num;
        }

        public int Number { get; }

        [CopierMethod]
        public static object DeepCopy(object original, ICopyContext context)
        {
            var typed = (UndeserializableType) original;
            return new UndeserializableType(typed.Number);
        }

        [SerializerMethod]
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            var typed = (UndeserializableType) untypedInput;
            context.StreamWriter.Write(typed.Number);
        }

        [DeserializerMethod]
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            throw new NotSupportedException(FailureMessage);
        }
    }

    [Serializable]
    public class UnserializableType
    {
        [CopierMethod]
        public static object DeepCopy(object original, ICopyContext context)
        {
            return original;
        }

        [SerializerMethod]
        public static void Serialize(object untypedInput, ISerializationContext context, Type expected)
        {
            throw new NotSupportedException(UndeserializableType.FailureMessage);
        }

        [DeserializerMethod]
        public static object Deserialize(Type expected, IDeserializationContext context)
        {
            return null;
        }
    }
}