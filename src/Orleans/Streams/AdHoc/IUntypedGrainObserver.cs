namespace Orleans.Streams.AdHoc
{
    using System;
    using System.Threading.Tasks;

    using Orleans.Runtime;

    /// <summary>
    /// Represents an untyped observer.
    /// </summary>
    internal interface IUntypedGrainObserver : IAddressable
    {
        /// <summary>
        /// Called when a new value has been produced in the specified stream.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <param name="value">The value.</param>
        /// <param name="token">The stream sequence token/</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnNextAsync(Guid streamId, object value, StreamSequenceToken token);

        /// <summary>
        /// Called when specified stream has terminated with an error.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <param name="exception">The exception.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnErrorAsync(Guid streamId, Exception exception);

        /// <summary>
        /// Called when the specified stream has completed.
        /// </summary>
        /// <param name="streamId">The stream id.</param>
        /// <returns>A <see cref="Task"/> representing the work performed.</returns>
        Task OnCompletedAsync(Guid streamId);
    }
}