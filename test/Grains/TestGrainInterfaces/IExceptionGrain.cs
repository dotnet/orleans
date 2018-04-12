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
        Task<object> EchoObject(object input);

        Task GetUnserializableObjectChained();

        Task<string> GetSiloIdentity();
    }
}