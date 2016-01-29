using System;
using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    public class ExceptionGrain : Grain, IExceptionGrain
    {
        /// <summary>
        /// Returns a canceled <see cref="Task"/>.
        /// </summary>
        /// <returns>A canceled <see cref="Task"/>.</returns>
        public Task Canceled()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.TrySetCanceled();
            return tcs.Task;
        }

        public async Task ThrowsInvalidOperationException()
        {
            await Task.Delay(0);
            throw new InvalidOperationException("Test exception");
        }

        public async Task ThrowsAggregateExceptionWrappingInvalidOperationException()
        {
            await Task.Delay(0);
            ThrowsInvalidOperationException().Wait();
        }

        public async Task ThrowsNestedAggregateExceptionsWrappingInvalidOperationException()
        {
            await Task.Delay(0);
            ThrowsAggregateExceptionWrappingInvalidOperationException().Wait();
        }

        public Task GrainCallToThrowsInvalidOperationException(long otherGrainId)
        {
            var otherGrain = GrainFactory.GetGrain<IExceptionGrain>(otherGrainId);
            return otherGrain.ThrowsInvalidOperationException();
        }

        public Task GrainCallToThrowsAggregateExceptionWrappingInvalidOperationException(long otherGrainId)
        {
            var otherGrain = GrainFactory.GetGrain<IExceptionGrain>(otherGrainId);
            return otherGrain.ThrowsAggregateExceptionWrappingInvalidOperationException();
        }
    }
}