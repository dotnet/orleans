namespace Orleans.Serialization.Invocation
{
    /// <summary>
    /// Represents a fulfillable promise for a response to a request.
    /// </summary>
    public interface IResponseCompletionSource
    {
        /// <summary>
        /// Completes the promise and takes ownership of the response envelope.
        /// </summary>
        /// <param name="value">The response whose ownership is transferred to this instance.</param>
        /// <remarks>
        /// Typed completion extracts the payload and disposes the envelope. Untyped completion transfers a successful
        /// envelope to its result consumer, which disposes it after use.
        /// </remarks>
        void Complete(Response value);

        /// <summary>
        /// Sets the result to the default value.
        /// </summary>
        void Complete();
    }
}
