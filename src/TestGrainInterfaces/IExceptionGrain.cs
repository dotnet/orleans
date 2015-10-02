namespace UnitTests.GrainInterfaces
{
    using System.Threading.Tasks;

    using Orleans;

    /// <summary>
    /// The ExceptionGrain interface.
    /// </summary>
    public interface IExceptionGrain : IGrainWithIntegerKey
    {
        /// <summary>
        /// Returns a canceled <see cref="Task"/>.
        /// </summary>
        /// <returns>A canceled <see cref="Task"/>.</returns>
        Task Cancelled();
    }
}