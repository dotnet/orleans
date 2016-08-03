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

        Task ThrowsAggregateExceptionWrappingInvalidOperationException();

        Task ThrowsNestedAggregateExceptionsWrappingInvalidOperationException();

        Task GrainCallToThrowsInvalidOperationException(long otherGrainId);

        Task GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(long otherGrainId);

        Task ThrowsSynchronousInvalidOperationException();

        Task ThrowsMultipleExceptionsAggregatedInFaultedTask();

        Task ThrowsSynchronousAggregateExceptionWithMultipleInnerExceptions();
    }
}