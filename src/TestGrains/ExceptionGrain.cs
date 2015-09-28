namespace UnitTests.Grains
{
    using System.Threading.Tasks;

    using Orleans;

    using UnitTests.GrainInterfaces;

    public class ExceptionGrain : Grain, IExceptionGrain
    {
        /// <summary>
        /// Returns a canceled <see cref="Task"/>.
        /// </summary>
        /// <returns>A canceled <see cref="Task"/>.</returns>
        public Task Cancelled()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.TrySetCanceled();
            return tcs.Task;
        }
    }
}